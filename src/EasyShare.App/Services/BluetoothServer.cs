using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using EasyShare.Core.Models;

namespace EasyShare.App.Services;

public class BluetoothServer : IDisposable
{
    private static readonly Guid ServiceUuid = new("e8a1c470-6d5f-4e8a-9b23-a7f06e9c1d2e");

    private object? _serviceProvider;
    private StreamSocketListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private readonly Dictionary<string, PendingUpload> _pending = new();
    private string _savePath = "";
    private string _alias = "EasyShare";

    private sealed class PendingUpload
    {
        public string BtAddress { get; set; } = "";
        public List<FileEntry> Files { get; set; } = new();
        public bool Accepted { get; set; }
        public bool Rejected { get; set; }
    }

    public event EventHandler<BluetoothUploadRequestEventArgs>? UploadRequested;
    public event EventHandler<BluetoothProgressEventArgs>? UploadProgress;
    public event EventHandler<BluetoothCompletedEventArgs>? UploadCompleted;

    public bool IsRunning { get; private set; }

    public void Start(string alias, string fingerprint, string savePath)
    {
        _alias = alias;
        _savePath = string.IsNullOrEmpty(savePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "EasyShare")
            : savePath;
        Directory.CreateDirectory(_savePath);

        _cts = new CancellationTokenSource();
        try
        {
            var serviceId = RfcommServiceId.FromUuid(ServiceUuid);
            var createMethod = typeof(RfcommServiceProvider).GetMethod("Create", [typeof(RfcommServiceId)]);
            if (createMethod == null) { IsRunning = false; return; }
            _serviceProvider = createMethod.Invoke(null, [serviceId]);

            var advertiseMethod = _serviceProvider!.GetType().GetMethod("StartAdvertising",
                [typeof(string), typeof(bool)]);
            advertiseMethod?.Invoke(_serviceProvider, [_alias, false]);

            var sidProp = _serviceProvider.GetType().GetProperty("ServiceId");
            var sidObj = sidProp?.GetValue(_serviceProvider);
            string serviceName = sidObj is RfcommServiceId sid ? sid.AsString() : "1";

            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;
            _listener.BindServiceNameAsync(serviceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask().Wait();
            IsRunning = true;
        }
        catch
        {
            IsRunning = false;
        }
    }

    private async void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
    {
        try { await HandleConnectionAsync(args.Socket, _cts!.Token); }
        catch { }
    }

    private async Task HandleConnectionAsync(StreamSocket socket, CancellationToken ct)
    {
        var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        var writer = new DataWriter(socket.OutputStream);

        var headerLine = await ReadLineAsync(reader, ct);
        if (headerLine != "EASYSHARE/BT1") return;

        var fingerprintLine = await ReadLineAsync(reader, ct);

        while (!ct.IsCancellationRequested)
        {
            var commandLine = await ReadLineAsync(reader, ct);
            if (commandLine is null or "QUIT" or "") break;

            if (commandLine.StartsWith("PREPARE "))
            {
                var json = commandLine["PREPARE ".Length..];
                var request = JsonSerializer.Deserialize<BluetoothPrepareRequest>(json);
                if (request is null || request.Files.Count == 0) break;

                var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
                var pending = new PendingUpload
                {
                    BtAddress = socket.Information.RemoteHostName?.CanonicalName ?? "",
                    Files = request.Files.Values.ToList(),
                };
                lock (_lock) _pending[sessionId] = pending;

                UploadRequested?.Invoke(this, new BluetoothUploadRequestEventArgs
                {
                    SessionId = sessionId,
                    SenderAlias = request.Alias,
                    Fingerprint = fingerprintLine?.StartsWith("Fingerprint: ") == true
                        ? fingerprintLine["Fingerprint: ".Length..] : "",
                    Files = pending.Files,
                    BtAddress = pending.BtAddress,
                });

                var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    while (!waitCts.Token.IsCancellationRequested)
                    {
                        lock (_lock)
                        {
                            if (pending.Accepted || pending.Rejected) break;
                        }
                        await Task.Delay(200, waitCts.Token);
                    }
                }
                catch (OperationCanceledException) { }

                lock (_lock) _pending.Remove(sessionId);

                var response = pending.Accepted ? "ACCEPT " + sessionId : "REJECT";
                await WriteLnAsync(writer, response);

                if (pending.Rejected || !pending.Accepted) break;
                continue;
            }

            if (commandLine.StartsWith("UPLOAD "))
            {
                var parts = commandLine.Split(' ');
                if (parts.Length < 4) break;
                var sessionId = parts[1];
                var fileId = parts[2];
                if (!long.TryParse(parts[3], out var fileSize)) break;

                await WriteLnAsync(writer, "READY");

                var fileEntry = _pending.Values.FirstOrDefault(p => true)?.Files.FirstOrDefault(f => f.Id == fileId);
                var safeName = SanitizeFileName(fileEntry?.FileName ?? fileId);
                var filePath = GetUniquePath(_savePath, safeName);
                long bytesReceived = 0;

                try
                {
                    await using (var fileStream = File.Create(filePath))
                    {
                        var remaining = fileSize;
                        var chunk = new byte[65536];
                        while (remaining > 0 && !ct.IsCancellationRequested)
                        {
                            var toRead = (uint)Math.Min(chunk.Length, remaining);
                            var loaded = await reader.LoadAsync(toRead);
                            if (loaded == 0) break;
                            var read = (int)Math.Min(loaded, remaining);
                            var readBuf = new byte[read];
                            reader.ReadBytes(readBuf);
                            await fileStream.WriteAsync(readBuf, ct);
                            bytesReceived += read;
                            remaining -= read;

                            UploadProgress?.Invoke(this, new BluetoothProgressEventArgs
                            {
                                FileName = safeName,
                                BytesReceived = bytesReceived,
                                TotalBytes = fileSize,
                            });
                        }
                    }

                    await WriteLnAsync(writer, "DONE");

                    UploadCompleted?.Invoke(this, new BluetoothCompletedEventArgs
                    {
                        FileName = safeName,
                        Size = bytesReceived,
                        SavePath = filePath,
                    });
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(filePath); } catch { }
                    await WriteLnAsync(writer, "CANCEL");
                }
                catch
                {
                    try { File.Delete(filePath); } catch { }
                    break;
                }
            }
        }
    }

    private static async Task WriteLnAsync(DataWriter writer, string line)
    {
        writer.WriteString(line + "\n");
        await writer.StoreAsync();
        await writer.FlushAsync();
    }

    private static async Task<string?> ReadLineAsync(DataReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (reader.UnconsumedBufferLength == 0)
            {
                var loaded = await reader.LoadAsync(1);
                if (loaded == 0) return null;
            }
            var b = reader.ReadByte();
            if (b == '\n') return sb.ToString();
            if (b != '\r') sb.Append((char)b);
        }
    }

    public void AcceptUpload(string sessionId)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(sessionId, out var p))
                p.Accepted = true;
        }
    }

    public void RejectUpload(string sessionId)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(sessionId, out var p))
                p.Rejected = true;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string GetUniquePath(string dir, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 0; i < 10000; i++)
        {
            var candidate = i == 0
                ? Path.Combine(dir, fileName)
                : Path.Combine(dir, $"{name} ({i}){ext}");
            try
            {
                using var fs = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return candidate;
            }
            catch (IOException) { }
        }
        throw new IOException($"Kein freier Dateiname fuer \"{fileName}\" verfuegbar.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        if (_serviceProvider is not null)
        {
            try
            {
                _serviceProvider.GetType().GetMethod("StopAdvertising")?.Invoke(_serviceProvider, null);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _listener?.Dispose();
    }
}

public class BluetoothUploadRequestEventArgs : EventArgs
{
    public string SessionId { get; set; } = "";
    public string SenderAlias { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public List<FileEntry> Files { get; set; } = new();
    public string BtAddress { get; set; } = "";
}

public class BluetoothProgressEventArgs : EventArgs
{
    public string FileName { get; set; } = "";
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
}

public class BluetoothCompletedEventArgs : EventArgs
{
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string SavePath { get; set; } = "";
}

public class BluetoothPrepareRequest
{
    public string Alias { get; set; } = "";
    public Dictionary<string, FileEntry> Files { get; set; } = new();
}
