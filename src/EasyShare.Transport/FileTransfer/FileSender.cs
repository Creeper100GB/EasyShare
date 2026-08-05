using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EasyShare.Core.Models;

namespace EasyShare.Transport.FileTransfer;

public class FileSender : IDisposable
{
    private readonly string _apiBase;
    private readonly string _targetFingerprint;
    private readonly int _targetPort;
    private readonly string _targetIp;
    private readonly DeviceAnnouncement _localInfo;
    private readonly HttpClient _client;
    private readonly bool _useTls;

    public double CurrentBytesPerSecond { get; private set; }

    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<TransferStatus>? StatusChanged;

    public FileSender(DeviceAnnouncement localInfo, string targetIp, int targetPort, string targetFingerprint, bool useTls, string apiBase)
    {
        _localInfo = localInfo;
        _targetIp = targetIp;
        _targetPort = targetPort;
        _targetFingerprint = targetFingerprint;
        _useTls = useTls;
        _apiBase = apiBase;

        var handler = new HttpClientHandler();
        if (_useTls)
        {
            handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
        }
        else
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _client = new HttpClient(handler);
        _client.Timeout = TimeSpan.FromMinutes(30);
    }

    private bool ValidateCertificate(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
    {
        if (cert is null) return false;
        using var sha = SHA256.Create();
        var fingerprint = Convert.ToHexStringLower(sha.ComputeHash(cert.RawData));
        return string.Equals(fingerprint, _targetFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public async Task SendAsync(TransferSession session, CancellationToken ct = default)
    {
        StatusChanged?.Invoke(this, TransferStatus.Active);
        var scheme = _useTls ? "https" : "http";
        var baseUrl = $"{scheme}://{_targetIp}:{_targetPort}";

        try
        {
            var prepare = await PrepareUploadAsync(baseUrl, session, ct);
            if (prepare is null || prepare.Files.Count == 0)
            {
                StatusChanged?.Invoke(this, TransferStatus.Rejected);
                return;
            }

            for (int i = 0; i < session.Files.Count; i++)
            {
                var file = session.Files[i];
                var token = prepare.Files.TryGetValue(file.Id, out var t) ? t : throw new KeyNotFoundException($"Server did not return token for file {file.Id}");
                await UploadFileAsync(baseUrl, prepare.SessionId, file, token, ct);

                var totalProgress = (double)(i + 1) / session.Files.Count;
                ProgressChanged?.Invoke(this, totalProgress);
            }

            StatusChanged?.Invoke(this, TransferStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke(this, TransferStatus.Cancelled);
            throw;
        }
        catch
        {
            StatusChanged?.Invoke(this, TransferStatus.Failed);
            throw;
        }
    }

    private async Task<PrepareUploadResponse?> PrepareUploadAsync(string baseUrl, TransferSession session, CancellationToken ct)
    {
        var request = new PrepareUploadRequest
        {
            Info = _localInfo,
            Files = session.Files.ToDictionary(f => f.Id, f => f),
        };

        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync($"{baseUrl}{_apiBase}/prepare-upload", content, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<PrepareUploadResponse>(responseJson);
    }

    private async Task UploadFileAsync(string baseUrl, string sessionId, FileEntry file, string token, CancellationToken ct)
    {
        await using var fileStream = File.OpenRead(file.LocalFilePath!);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(sessionId), "sessionId");
        form.Add(new StringContent(file.Id), "fileId");
        form.Add(new StringContent(token), "token");
        form.Add(new StreamContent(fileStream), "file", file.FileName);

        var sw = Stopwatch.StartNew();

        using var response = await _client.PostAsync($"{baseUrl}{_apiBase}/upload", form, ct);
        response.EnsureSuccessStatusCode();
        sw.Stop();

        if (sw.Elapsed.TotalSeconds > 0)
            CurrentBytesPerSecond = file.Size / sw.Elapsed.TotalSeconds;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}