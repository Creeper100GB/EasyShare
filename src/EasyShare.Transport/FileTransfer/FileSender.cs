using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EasyShare.Core.Logging;
using EasyShare.Core.Models;
using Serilog;

namespace EasyShare.Transport.FileTransfer;

public class FileSender : IDisposable
{
    private static readonly Serilog.ILogger Log = EasyLogger.Log.ForContext("SourceContext", "FileSender");
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

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
    private CancellationTokenSource? _idleCts;

    public double CurrentBytesPerSecond { get; private set; }
    public long TotalBytes => _totalBytes;
    public long BytesSent => _bytesSent;

    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<TransferStatus>? StatusChanged;
    public TransferStatus? LastStatus { get; private set; }
    public bool WasConnectionError { get; private set; }

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
            Log.Warning("TLS deaktiviert - Verbindung ohne Zertifikatsvalidierung an {TargetIp}", targetIp);
        }

        handler.UseProxy = false;
        handler.AutomaticDecompression = System.Net.DecompressionMethods.None;

        _client = new HttpClient(handler);
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _client.DefaultRequestHeaders.ExpectContinue = false;

        Log.Debug("FileSender erstellt: {TargetIp}:{TargetPort}, TLS={UseTls}", targetIp, targetPort, useTls);
    }

    private async Task<string> CreateTempZipAsync(List<FileEntry> files, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "EasyShare", "zip");
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, $"EasyShare_{Guid.NewGuid():N}.zip");

        Log.Debug("Erstelle Temp-Zip: {Count} Dateien", files.Count);

        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (file.LocalFilePath != null && File.Exists(file.LocalFilePath))
                {
                    var entryName = file.FileName;
                    zip.CreateEntryFromFile(file.LocalFilePath, entryName, CompressionLevel.NoCompression);
                }
            }
        }, ct);

        return zipPath;
    }

    private bool ValidateCertificate(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
    {
        if (cert is null) return false;
        using var sha = SHA256.Create();
        var fingerprint = Convert.ToHexStringLower(sha.ComputeHash(cert.RawData));
        return string.Equals(fingerprint, _targetFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public async Task SendAsync(TransferSession session, CancellationToken ct = default, bool compress = false)
    {
        StatusChanged?.Invoke(this, TransferStatus.Active);
        LastStatus = TransferStatus.Active;
        WasConnectionError = false;
        var files = session.Files.ToList();
        _bytesSent = 0;
        _lastSpeedBytes = 0;
        _lastSpeedTimestamp = Stopwatch.GetTimestamp();
        _lastProgressTicks = 0;
        var scheme = _useTls ? "https" : "http";
        var baseUrl = $"{scheme}://{_targetIp}:{_targetPort}";

        Log.Information("Transfer starten: {TargetIp}:{TargetPort}, {FileCount} Dateien, {TotalSize} Bytes",
            _targetIp, _targetPort, files.Count, files.Sum(f => f.Size));

        _idleCts = new CancellationTokenSource();
        _idleCts.CancelAfter(IdleTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _idleCts.Token);

        string? tempZipPath = null;
        var compressed = session.ContainsFolders || (compress && files.Count > 1);
        var originalFileCount = files.Count;
        try
        {
            if (compressed)
            {
                tempZipPath = await CreateTempZipAsync(files, linkedCts.Token);
                var entryFileName = session.ZipName is not null
                    ? $"{session.ZipName}.zip"
                    : Path.GetFileName(tempZipPath);
                files = new List<FileEntry> { new FileEntry
                {
                    Id = "zip",
                    FileName = entryFileName,
                    Size = new FileInfo(tempZipPath).Length,
                    LocalFilePath = tempZipPath,
                }};
            }

            _totalBytes = files.Sum(f => f.Size);

            Log.Debug("Prepare-Upload an {BaseUrl}", baseUrl);
            var prepare = await PrepareUploadAsync(baseUrl, files, linkedCts.Token, compressed, originalFileCount);
            if (prepare is null || prepare.Files.Count == 0)
            {
                Log.Warning("Upload abgelehnt/Timeout von {TargetIp}", _targetIp);
                StatusChanged?.Invoke(this, TransferStatus.Rejected);
                LastStatus = TransferStatus.Rejected;
                return;
            }

            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var token = prepare.Files.TryGetValue(file.Id, out var t) ? t : throw new KeyNotFoundException($"Server did not return token for file {file.Id}");
                Log.Debug("Upload Datei {FileId} ({FileName}, {Size} Bytes) an {BaseUrl}", file.Id, file.FileName, file.Size, baseUrl);
                await UploadFileAsync(baseUrl, prepare.SessionId, file, token, linkedCts.Token);
            }

            Log.Information("Transfer erfolgreich: {BytesSent} Bytes an {TargetIp}", _bytesSent, _targetIp);
            ProgressChanged?.Invoke(this, 1.0);
            StatusChanged?.Invoke(this, TransferStatus.Completed);
            LastStatus = TransferStatus.Completed;
        }
        catch (OperationCanceledException) when (_idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Log.Warning("Idle-Timeout: keine Daten fuer {Timeout}s an {TargetIp}", IdleTimeout.TotalSeconds, _targetIp);
            WasConnectionError = true;
            StatusChanged?.Invoke(this, TransferStatus.ConnectionFailed);
            LastStatus = TransferStatus.ConnectionFailed;
            throw new HttpRequestException($"Transfer gestoppt - keine Daten fuer {IdleTimeout.TotalSeconds:0}s empfangen (Idle-Timeout)");
        }
        catch (OperationCanceledException)
        {
            Log.Information("Transfer vom Nutzer abgebrochen an {TargetIp} ({BytesSent} Bytes gesendet)", _targetIp, _bytesSent);
            StatusChanged?.Invoke(this, TransferStatus.Cancelled);
            LastStatus = TransferStatus.Cancelled;
            throw;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "Verbindungsfehler an {TargetIp} ({BytesSent} Bytes gesendet)", _targetIp, _bytesSent);
            WasConnectionError = true;
            StatusChanged?.Invoke(this, TransferStatus.ConnectionFailed);
            LastStatus = TransferStatus.ConnectionFailed;
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Transfer fehlgeschlagen an {TargetIp} ({BytesSent}/{TotalBytes} Bytes)", _targetIp, _bytesSent, _totalBytes);
            StatusChanged?.Invoke(this, TransferStatus.Failed);
            LastStatus = TransferStatus.Failed;
            throw;
        }
        finally
        {
            _idleCts.Cancel();
            _idleCts.Dispose();
            _idleCts = null;
            if (tempZipPath != null && File.Exists(tempZipPath))
                try { File.Delete(tempZipPath); }
                catch (Exception ex) { Log.Debug(ex, "Temp-Zip Cleanup fehlgeschlagen: {Path}", tempZipPath); }
        }
    }

    private async Task<PrepareUploadResponse?> PrepareUploadAsync(string baseUrl, List<FileEntry> files, CancellationToken ct, bool compressed, int originalFileCount)
    {
        var request = new PrepareUploadRequest
        {
            Info = _localInfo,
            Files = files.ToDictionary(f => f.Id, f => f),
            Compressed = compressed,
            OriginalFileCount = originalFileCount,
        };

        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync($"{baseUrl}{_apiBase}/prepare-upload", content, ct);

        if ((int)response.StatusCode is 403 or 408)
        {
            Log.Debug("Prepare-Upload Antwort: {StatusCode} von {BaseUrl}", response.StatusCode, baseUrl);
            return null;
        }

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
        {
            Log.Warning("Upload von {FileName} abgebrochen durch Empfaenger (HTTP 499)", file.FileName);
            throw new OperationCanceledException("Receiver cancelled the upload");
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Error("Upload von {FileName} fehlgeschlagen: HTTP {StatusCode} - {Body}", file.FileName, (int)response.StatusCode, body);
        }
        response.EnsureSuccessStatusCode();
    }

    private void OnBytesSent(long bytes)
    {
        if (bytes <= 0) return;
        _bytesSent += bytes;

        _idleCts?.CancelAfter(IdleTimeout);

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
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set
            {
                if (value != 0)
                    throw new NotSupportedException("Seeking beyond position 0 is not supported during upload.");
                if (_inner.Position != 0)
                    _inner.Seek(0, SeekOrigin.Begin);
            }
        }

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
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (offset == 0 && origin == SeekOrigin.Begin)
            {
                var result = _inner.Seek(0, SeekOrigin.Begin);
                return result;
            }
            throw new NotSupportedException("Seeking beyond position 0 is not supported during upload.");
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
