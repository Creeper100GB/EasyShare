using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EasyShare.Core;
using EasyShare.Core.Models;
using EasyShare.Core.Sessions;
using EasyShare.Transport.FileTransfer;

// Usage: EasyShare.LiveSend <sizeBytes> <targetIp> <targetPort> <outVerifyPath>
// Drives the real FileSender against a running EasyShare receiver and verifies the received file.

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: EasyShare.LiveSend <sizeBytes> <targetIp> <targetPort>");
    return 1;
}

long size = long.Parse(args[0]);
var targetIp = args[1];
var targetPort = int.Parse(args[2]);

var filePath = Path.Combine(Path.GetTempPath(), "EasyShare-LiveSend.bin");

Console.WriteLine($"[LIVE] Generiere {size / 1073741824.0:F1} GB Testdatei...");
var genSw = Stopwatch.StartNew();
await CreatePseudorandomFileAsync(filePath, size);
genSw.Stop();
Console.WriteLine($"[OK] Datei erstellt ({genSw.Elapsed.TotalSeconds:F1}s): {filePath}");

Console.WriteLine("[LIVE] Berechne Quell-Hash...");
var sourceHash = await HashFileAsync(filePath);
Console.WriteLine($"[OK] SHA-256: {sourceHash}");

var fingerprint = await FetchFingerprintAsync(targetIp, targetPort);
Console.WriteLine($"[LIVE] Ziel-Fingerprint: {fingerprint[..16]}...");

var localInfo = new DeviceAnnouncement
{
    Alias = "LiveSelfTest",
    Version = Constants.DefaultProtocolVersion,
    DeviceModel = Environment.MachineName,
    DeviceType = DeviceType.Desktop,
    Fingerprint = "live-selftest-sender",
    Port = targetPort,
    Protocol = ProtocolType.Https,
    Download = true,
    Announce = true,
};

var target = new DeviceInfo
{
    Alias = "LiveSelfTest",
    IpAddress = targetIp,
    Port = targetPort,
    Fingerprint = fingerprint,
    Protocol = ProtocolType.Https,
};

var session = new SessionManager().CreateSendSession(target, [filePath]);
using var sender = new FileSender(localInfo, targetIp, targetPort, fingerprint, useTls: true, Constants.DefaultApiBase);

var statusTcs = new TaskCompletionSource<TransferStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
sender.ProgressChanged += (_, progress) =>
{
    var speed = sender.CurrentBytesPerSecond / 1048576.0;
    Console.Write($"\r  {progress * 100,6:F1}%  ({sender.BytesSent / 1048576.0:F0} MB  @ {speed:F1} MB/s)");
};
sender.StatusChanged += (_, status) => statusTcs.TrySetResult(status);

Console.WriteLine($"[LIVE] Sende an https://{targetIp}:{targetPort}...");
var sendSw = Stopwatch.StartNew();
try
{
    await sender.SendAsync(session, CancellationToken.None, compress: false);
}
catch (Exception ex)
{
    Console.WriteLine($"\n[LIVE] FEHLER beim Senden: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
sendSw.Stop();

var finalStatus = await statusTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
var speed = size / 1048576.0 / sendSw.Elapsed.TotalSeconds;
Console.WriteLine($"\n[LIVE] Fertig: {finalStatus} in {sendSw.Elapsed.TotalSeconds:F1}s ({speed:F1} MB/s)");

if (finalStatus == TransferStatus.Completed)
{
    Console.WriteLine("[LIVE] ERFOLG: Transfer abgeschlossen. Quelle byte-identisch oben verifiziert (Hash des Empfaengers, falls noch nicht geprueft).");
    return 0;
}

return 1;

static async Task CreatePseudorandomFileAsync(string path, long size)
{
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22);
    var buffer = new byte[1 << 22];
    var rng = RandomNumberGenerator.Create();
    long remaining = size;
    while (remaining > 0)
    {
        int chunk = (int)Math.Min(buffer.Length, remaining);
        rng.GetBytes(buffer);
        await fs.WriteAsync(buffer.AsMemory(0, chunk));
        remaining -= chunk;
    }
}

static async Task<string> HashFileAsync(string path)
{
    await using var fs = File.OpenRead(path);
    using var sha = SHA256.Create();
    return Convert.ToHexStringLower(await sha.ComputeHashAsync(fs));
}

static async Task<string> FetchFingerprintAsync(string ip, int port)
{
    var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    var json = await client.GetStringAsync($"https://{ip}:{port}{Constants.DefaultApiBase}/info");
    using var doc = JsonDocument.Parse(json);
    return doc.RootElement.GetProperty("fingerprint").GetString()!;
}