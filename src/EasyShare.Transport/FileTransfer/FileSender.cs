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

    private long _totalBytes;
    private long _bytesSent;
    private long _lastSpeedBytes;
    private long _lastSpeedTimestamp;
    private long _lastProgressTicks;

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
        _client.Timeout = Timeout.InfiniteTimeSpan;
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
        _totalBytes = session.Files.Sum(f => f.Size);
        _bytesSent = 0;
        _lastSpeedBytes = 0;
        _lastSpeedTimestamp = Stopwatch.GetTimestamp();
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
            }

            ProgressChanged?.Invoke(this, 1.0);
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
        using var progressStream = new ProgressStream(fileStream, OnBytesSent);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(sessionId), "sessionId");
        form.Add(new StringContent(file.Id), "fileId");
        form.Add(new StringContent(token), "token");
        form.Add(new StreamContent(progressStream), "file", file.FileName);

        using var response = await _client.PostAsync($"{baseUrl}{_apiBase}/upload", form, ct);
        if ((int)response.StatusCode == 499)
            throw new OperationCanceledException("Receiver cancelled the upload");
        response.EnsureSuccessStatusCode();
    }

    private void OnBytesSent(long bytes)
    {
        if (bytes <= 0) return;
        _bytesSent += bytes;

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _lastSpeedTimestamp) / (double)Stopwatch.Frequency;
        if (elapsedSeconds >= 0.5)
        {
            CurrentBytesPerSecond = (_bytesSent - _lastSpeedBytes) / elapsedSeconds;
            _lastSpeedBytes = _bytesSent;
            _lastSpeedTimestamp = now;
        }

        var progressElapsedMs = (now - _lastProgressTicks) * 1000 / (double)Stopwatch.Frequency;
        if (_totalBytes > 0 && progressElapsedMs >= 200)
        {
            _lastProgressTicks = now;
            ProgressChanged?.Invoke(this, Math.Min(1.0, (double)_bytesSent / _totalBytes));
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onBytesRead;

        public ProgressStream(Stream inner, Action<long> onBytesRead)
        {
            _inner = inner;
            _onBytesRead = onBytesRead;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            if (n > 0) _onBytesRead(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int n = await _inner.ReadAsync(buffer, ct);
            if (n > 0) _onBytesRead(n);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int n = await _inner.ReadAsync(buffer, offset, count, ct);
            if (n > 0) _onBytesRead(n);
            return n;
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
