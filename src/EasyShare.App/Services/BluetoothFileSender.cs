using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using EasyShare.Core.Logging;
using EasyShare.Core.Models;
using Serilog;

namespace EasyShare.App.Services;

public class BluetoothFileSender : IDisposable
{
    private static readonly ILogger Log = EasyLogger.Log.ForContext<BluetoothFileSender>();

    private static readonly Guid ServiceUuid = new("e8a1c470-6d5f-4e8a-9b23-a7f06e9c1d2e");

    private readonly string _localFingerprint;
    private readonly string _localAlias;

    public double CurrentBytesPerSecond { get; private set; }
    public long TotalBytes { get; private set; }
    public long BytesSent { get; private set; }

    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<TransferStatus>? StatusChanged;

    public BluetoothFileSender(string localAlias, string localFingerprint)
    {
        _localAlias = localAlias;
        _localFingerprint = localFingerprint;
    }

    public async Task SendAsync(TransferSession session, string btAddress, CancellationToken ct = default)
    {
        StatusChanged?.Invoke(this, TransferStatus.Active);
        var files = session.Files.ToList();
        BytesSent = 0;
        TotalBytes = files.Sum(f => f.Size);

        try
        {
            var device = await BluetoothDevice.FromBluetoothAddressAsync(
                ulong.Parse(btAddress, System.Globalization.NumberStyles.HexNumber));
            if (device is null) throw new SocketException((int)System.Net.Sockets.SocketError.HostNotFound);

            var services = await device.GetRfcommServicesForIdAsync(
                RfcommServiceId.FromUuid(ServiceUuid));
            var service = services.Services.FirstOrDefault();
            if (service is null) throw new SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);

            using var socket = new StreamSocket();
            socket.Control.OutboundBufferSizeInBytes = 65536;
            await socket.ConnectAsync(
                device.HostName,
                service.ConnectionServiceName,
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
            var writer = new DataWriter(socket.OutputStream);

            await WriteLnAsync(writer, "EASYSHARE/BT1");
            await WriteLnAsync(writer, $"Fingerprint: {_localFingerprint}");

            var request = new BluetoothPrepareRequest
            {
                Alias = _localAlias,
                Files = files.ToDictionary(f => f.Id, f => f),
            };
            var json = JsonSerializer.Serialize(request);
            await WriteLnAsync(writer, $"PREPARE {json}");

            var response = await ReadLineAsync(reader, ct);
            if (response is null || !response.StartsWith("ACCEPT"))
            {
                StatusChanged?.Invoke(this, TransferStatus.Rejected);
                return;
            }

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                await WriteLnAsync(writer, $"UPLOAD {file.Id} {file.Id} {file.Size}");

                var ready = await ReadLineAsync(reader, ct);
                if (ready != "READY") break;

                await using var fileStream = File.OpenRead(file.LocalFilePath!);
                var chunk = new byte[65536];
                long fileBytesSent = 0;
                var lastSpeedTime = sw.ElapsedTicks;
                var lastSpeedBytes = 0L;

                while (fileBytesSent < file.Size && !ct.IsCancellationRequested)
                {
                    var toRead = (int)Math.Min(chunk.Length, file.Size - fileBytesSent);
                    var n = await fileStream.ReadAsync(chunk.AsMemory(0, toRead), ct);
                    if (n <= 0) break;

                    var sendBuf = new byte[n];
                    Array.Copy(chunk, 0, sendBuf, 0, n);
                    writer.WriteBytes(sendBuf);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    fileBytesSent += n;
                    BytesSent += n;

                    var now = sw.ElapsedTicks;
                    var elapsed = (now - lastSpeedTime) / (double)Stopwatch.Frequency;
                    if (elapsed >= 0.5)
                    {
                        CurrentBytesPerSecond = (BytesSent - lastSpeedBytes) / elapsed;
                        lastSpeedBytes = BytesSent;
                        lastSpeedTime = now;
                    }

                    if (TotalBytes > 0)
                        ProgressChanged?.Invoke(this, Math.Min(1.0, (double)BytesSent / TotalBytes));
                }

                await ReadLineAsync(reader, ct);
            }

            await WriteLnAsync(writer, "QUIT");
            ProgressChanged?.Invoke(this, 1.0);
            StatusChanged?.Invoke(this, TransferStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Bluetooth-Transfer abgebrochen an {Address}", btAddress);
            StatusChanged?.Invoke(this, TransferStatus.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bluetooth-Transfer fehlgeschlagen an {Address}", btAddress);
            StatusChanged?.Invoke(this, TransferStatus.Failed);
            throw;
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
        while (!ct.IsCancellationRequested)
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
        return null;
    }

    public void Dispose() { }
}
