using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyShare.Core.Services;

public class UpdateInfo
{
    public string LatestVersion { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public bool UpdateAvailable { get; set; }
}

public class UpdateService
{
    private const string RepoOwner = "Creeper100GB";
    private const string RepoName = "EasyShare";
    private const string AssetPattern = "win-x64";
    private const long MaxAssetBytes = 5L * 1024 * 1024 * 1024;
    private const long MaxSingleEntryBytes = 10L * 1024 * 1024 * 1024;
    private const long MaxZipExpansionRatio = 100;
    private static readonly Regex ExecutableNameRegex = new(@"^[\w\-\.]+\.exe$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _installDir;
    private readonly string _currentVersion;

    public event Action<UpdateInfo>? UpdateCheckCompleted;

    public UpdateService(string currentVersion, string? installDir = null)
    {
        _currentVersion = currentVersion.TrimStart('v');
        _installDir = installDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyShare");
    }

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        var info = new UpdateInfo();

        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EasyShare");
            httpClient.Timeout = TimeSpan.FromSeconds(15);

            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            using var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            info.LatestVersion = tag.TrimStart('v');

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name != null && name.Contains(AssetPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        if (asset.TryGetProperty("browser_download_url", out var urlElement))
                            info.DownloadUrl = urlElement.GetString() ?? "";
                        break;
                    }
                }
            }

            info.UpdateAvailable = !string.IsNullOrEmpty(info.DownloadUrl)
                && CompareVersions(info.LatestVersion, _currentVersion) > 0;
        }
        catch
        {
            info.UpdateAvailable = false;
        }

        UpdateCheckCompleted?.Invoke(info);
    }

    public async Task DownloadAndApplyAsync(string downloadUrl, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), "EasyShare-update.zip");
        var tempExtract = Path.Combine(Path.GetTempPath(), "EasyShare-update");

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(10);

        try
        {
            var zipSize = await DownloadToFileAsync(httpClient, downloadUrl, tempZip, progress, ct);

            var expectedHash = await GetHashAssetAsync(httpClient, downloadUrl + ".sha256", ct);
            if (!VerifySha256File(tempZip, expectedHash))
                throw new InvalidDataException("SHA256-Prüfsumme des Update-Archivs konnte nicht bestätigt werden.");

            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, true);
            ExtractZipSafely(tempZip, tempExtract, zipSize);

            var sourceExe = Path.Combine(tempExtract, "EasyShare.exe");
            if (!File.Exists(sourceExe))
            {
                var exes = Directory.GetFiles(tempExtract, "*.exe");
                if (exes.Length == 0) return;
                sourceExe = exes[0];
            }

            var exeName = Path.GetFileName(sourceExe);
            if (!IsValidExecutableName(exeName))
                throw new InvalidDataException($"Ungültiger Name der ausführbaren Datei im Update: {exeName}");

            var scriptContent = "@echo off\r\n"
                + "setlocal\r\n"
                + "ping -n 3 127.0.0.1 >nul\r\n"
                + "taskkill /f /im \"" + exeName + "\" 2>nul\r\n"
                + "ping -n 5 127.0.0.1 >nul\r\n"
                + "xcopy /y /e /i \"" + tempExtract + "\\*\" \"" + _installDir + "\\\" >nul 2>&1\r\n"
                + "del /q \"" + tempZip + "\" >nul 2>&1\r\n"
                + "rd /s /q \"" + tempExtract + "\" >nul 2>&1\r\n"
                + "start \"\" \"" + _installDir + "\\" + exeName + "\"\r\n"
                + "del \"%~f0\"\r\n";

            var updateScript = Path.Combine(Path.GetTempPath(), "EasyShare-update.bat");
            File.WriteAllText(updateScript, scriptContent);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + updateScript + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        finally
        {
            if (File.Exists(tempZip))
                try { File.Delete(tempZip); } catch { }
        }
    }

    internal static bool IsValidExecutableName(string fileName)
        => ExecutableNameRegex.IsMatch(fileName);

    internal static bool VerifySha256File(string filePath, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash)) return false;
        expectedHash = expectedHash.Trim();
        var token = expectedHash.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return false;

        using var sha = SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var actual = Convert.ToHexStringLower(sha.ComputeHash(fs));
        return actual == token.ToLowerInvariant();
    }

    private static async Task<long> DownloadToFileAsync(HttpClient client, string url, string destPath, IProgress<int>? progress, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? 0L;
        if (totalBytes > MaxAssetBytes)
            throw new InvalidDataException("Update-Asset überschreitet die zulässige Größe.");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destPath);

        var buffer = new byte[81920];
        long read = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, ct)) > 0)
        {
            read += bytesRead;
            if (read > MaxAssetBytes)
                throw new InvalidDataException("Update-Asset überschreitet die zulässige Größe.");
            await dest.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            if (totalBytes > 0)
                progress?.Report((int)(read * 100 / totalBytes));
        }
        return read;
    }

    private static async Task<string?> GetHashAssetAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            return (await response.Content.ReadAsStringAsync(ct)).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void ExtractZipSafely(string zipPath, string extractDir, long zipSize)
    {
        Directory.CreateDirectory(extractDir);
        var extractRoot = Path.GetFullPath(extractDir);
        var maxTotal = Math.Max(zipSize * MaxZipExpansionRatio, 1L * 1024 * 1024 * 1024);
        long totalExtracted = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > MaxSingleEntryBytes)
                throw new InvalidDataException($"Update-Eintrag zu groß: {entry.FullName}");
            totalExtracted += entry.Length;
            if (totalExtracted > maxTotal)
                throw new InvalidDataException("Update-Archiv expandiert über das erlaubte Maß.");

            var target = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
            if (!target.StartsWith(extractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Update-Eintrag außerhalb des Zielverzeichnisses: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static int CompareVersions(string a, string b)
    {
        if (!Version.TryParse(a, out var vA)) return -1;
        if (!Version.TryParse(b, out var vB)) return -1;
        return vA.CompareTo(vB);
    }

}