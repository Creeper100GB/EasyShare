using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

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
            var args = "api repos/" + RepoOwner + "/" + RepoName + "/releases/latest";
            var json = await RunGhAsync(args, ct);

            if (string.IsNullOrEmpty(json))
            {
                UpdateCheckCompleted?.Invoke(info);
                return;
            }

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
        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? 0L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fs = File.Create(tempZip);

            var buffer = new byte[81920];
            long read = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                read += bytesRead;
                if (totalBytes > 0)
                    progress?.Report((int)(read * 100 / totalBytes));
            }

            if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, true);
            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            var sourceExe = Path.Combine(tempExtract, "EasyShare.exe");
            if (!File.Exists(sourceExe))
            {
                var exes = Directory.GetFiles(tempExtract, "*.exe");
                if (exes.Length == 0) return;
                sourceExe = exes[0];
            }

            var exeName = Path.GetFileName(sourceExe);
            var scriptContent = "@echo off\r\n"
                + "setlocal\r\n"
                + "ping -n 3 127.0.0.1 >nul\r\n"
                + "taskkill /f /im " + exeName + " 2>nul\r\n"
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

    private static int CompareVersions(string a, string b)
    {
        if (!Version.TryParse(a, out var vA)) return -1;
        if (!Version.TryParse(b, out var vB)) return -1;
        return vA.CompareTo(vB);
    }

    private static async Task<string> RunGhAsync(string args, CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output.Trim();
    }
}