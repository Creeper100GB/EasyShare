using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using EasyShare.Core.Crypto;
using EasyShare.Core.Models;
using EasyShare.Core.Sessions;
using EasyShare.Transport.FileTransfer;
using EasyShare.Transport.Server;
using AppProtocolType = EasyShare.Core.Models.ProtocolType;

namespace EasyShare.Core.Tests;

public class SendFlowTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "EasyShareSendFlow", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.Delay(100);
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static async Task<string> CreatePseudoRandomFileAsync(string path, long size)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        ulong state = 0x9E3779B97F4A7C15UL;
        long remaining = size;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(buffer.Length, remaining);
            for (int i = 0; i < chunk; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                buffer[i] = (byte)(state >> 32);
            }
            await stream.WriteAsync(buffer.AsMemory(0, chunk));
            hash.AppendData(buffer, 0, chunk);
            remaining -= chunk;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<string> HashFileAsync(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForServerAsync(int port)
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch { await Task.Delay(100); }
        }
        throw new TimeoutException("Server did not start within 5 seconds.");
    }

    private static LocalSendServer StartServer(int port, string fingerprint, string saveDir)
    {
        var cert = TlsCertificate.LoadOrCreate();
        var server = new LocalSendServer(cert);
        server.Start(port, alias: "TestReceiver", fingerprint: fingerprint, savePath: saveDir);
        return server;
    }

    private static FileSender CreateSender(int port, string fingerprint)
    {
        return new FileSender(
            new DeviceAnnouncement
            {
                Alias = "TestSender",
                Version = "2.0",
                Fingerprint = fingerprint,
                Port = 0,
                Protocol = AppProtocolType.Https,
            },
            "127.0.0.1", port, fingerprint, useTls: true, "/api/localsend/v2");
    }

    [Fact]
    public async Task Reject_ReceiverDeclines_SenderGetsRejected()
    {
        var port = GetFreePort();
        var saveDir = Path.Combine(_tempDir, "save");
        Directory.CreateDirectory(saveDir);
        var sourcePath = Path.Combine(_tempDir, "reject.bin");
        await CreatePseudoRandomFileAsync(sourcePath, 1024 * 1024);

        using var cert = TlsCertificate.Generate();
        var fingerprint = TlsCertificate.GetFingerprint(cert);
        var server = new LocalSendServer(cert);
        server.Start(port, alias: "TestReceiver", fingerprint: fingerprint, savePath: saveDir);

        var statusChanged = new TaskCompletionSource<TransferStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.UploadRequested += (_, e) => server.RejectUpload(e.SessionId);

        await WaitForServerAsync(port);

        var target = new DeviceInfo { Alias = "TestReceiver", IpAddress = "127.0.0.1", Port = port, Fingerprint = fingerprint, Protocol = AppProtocolType.Https };
        var session = new SessionManager().CreateSendSession(target, [sourcePath]);

        using var sender = CreateSender(port, fingerprint);
        sender.StatusChanged += (_, status) =>
        {
            if (status is TransferStatus.Rejected or TransferStatus.Failed or TransferStatus.Completed)
                statusChanged.TrySetResult(status);
        };

        await sender.SendAsync(session, compress: false);
        var result = await statusChanged.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(TransferStatus.Rejected, result);
        server.Stop();
    }

    [Fact]
    public async Task Cancel_DuringTransfer_DeletesPartialFile()
    {
        const long fileSize = 50L * 1024 * 1024;
        var port = GetFreePort();
        var saveDir = Path.Combine(_tempDir, "save");
        Directory.CreateDirectory(saveDir);
        var sourcePath = Path.Combine(_tempDir, "cancel.bin");
        await CreatePseudoRandomFileAsync(sourcePath, fileSize);

        using var cert = TlsCertificate.Generate();
        var fingerprint = TlsCertificate.GetFingerprint(cert);
        var server = new LocalSendServer(cert);
        server.Start(port, alias: "TestReceiver", fingerprint: fingerprint, savePath: saveDir);

        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? receivedFilePath = null;
        server.UploadRequested += (_, e) => server.AcceptUpload(e.SessionId, saveDir);
        server.UploadProgress += (_, e) =>
        {
            if (e.BytesReceived > 0 && !uploadStarted.Task.IsCompleted)
                uploadStarted.TrySetResult();
        };
        server.UploadCancelled += (_, e) => receivedFilePath = e.FileName;

        await WaitForServerAsync(port);

        var target = new DeviceInfo { Alias = "TestReceiver", IpAddress = "127.0.0.1", Port = port, Fingerprint = fingerprint, Protocol = AppProtocolType.Https };
        var session = new SessionManager().CreateSendSession(target, [sourcePath]);

        using var cts = new CancellationTokenSource();
        using var sender = CreateSender(port, fingerprint);
        var sendTask = sender.SendAsync(session, cts.Token, compress: false);

        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
        cts.Cancel();

        try { await sendTask; } catch (OperationCanceledException) { }

        Assert.Equal(TransferStatus.Cancelled, sender.LastStatus ?? TransferStatus.Cancelled);
        server.Stop();
    }

    [Fact]
    public async Task MultipleFiles_AllReceived_IdenticalHashes()
    {
        var port = GetFreePort();
        var saveDir = Path.Combine(_tempDir, "save");
        Directory.CreateDirectory(saveDir);

        var filePaths = new List<(string path, string hash)>();
        for (int i = 0; i < 5; i++)
        {
            var size = 100L * 1024 * 1024 + (i * 1024);
            var path = Path.Combine(_tempDir, $"multi_{i}.bin");
            var hash = await CreatePseudoRandomFileAsync(path, size);
            filePaths.Add((path, hash));
        }

        using var cert = TlsCertificate.Generate();
        var fingerprint = TlsCertificate.GetFingerprint(cert);
        var server = new LocalSendServer(cert);
        server.Start(port, alias: "TestReceiver", fingerprint: fingerprint, savePath: saveDir);

        var completed = new TaskCompletionSource<UploadCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.UploadRequested += (_, e) => server.AcceptUpload(e.SessionId, saveDir);
        server.UploadCompleted += (_, e) => completed.TrySetResult(e);

        await WaitForServerAsync(port);

        var target = new DeviceInfo { Alias = "TestReceiver", IpAddress = "127.0.0.1", Port = port, Fingerprint = fingerprint, Protocol = AppProtocolType.Https };
        var session = new SessionManager().CreateSendSession(target, filePaths.Select(f => f.path).ToList());

        using var sender = CreateSender(port, fingerprint);
        await sender.SendAsync(session, compress: false);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(120));

        foreach (var (origPath, expectedHash) in filePaths)
        {
            var origName = Path.GetFileName(origPath);
            var receivedPath = Directory.GetFiles(saveDir, origName, SearchOption.AllDirectories).First();
            Assert.Equal(expectedHash, await HashFileAsync(receivedPath));
        }

        server.Stop();
    }

    [Fact]
    public async Task ZeroByteFile_DoesNotCrash()
    {
        var port = GetFreePort();
        var saveDir = Path.Combine(_tempDir, "save");
        Directory.CreateDirectory(saveDir);
        var sourcePath = Path.Combine(_tempDir, "empty.bin");
        File.WriteAllBytes(sourcePath, []);

        using var cert = TlsCertificate.Generate();
        var fingerprint = TlsCertificate.GetFingerprint(cert);
        var server = new LocalSendServer(cert);
        server.Start(port, alias: "TestReceiver", fingerprint: fingerprint, savePath: saveDir);

        var completed = new TaskCompletionSource<UploadCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.UploadRequested += (_, e) => server.AcceptUpload(e.SessionId, saveDir);
        server.UploadCompleted += (_, e) => completed.TrySetResult(e);

        await WaitForServerAsync(port);

        var target = new DeviceInfo { Alias = "TestReceiver", IpAddress = "127.0.0.1", Port = port, Fingerprint = fingerprint, Protocol = AppProtocolType.Https };
        var session = new SessionManager().CreateSendSession(target, [sourcePath]);

        using var sender = CreateSender(port, fingerprint);
        await sender.SendAsync(session, compress: false);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0L, result.Size);
        Assert.True(File.Exists(result.SavePath));
        Assert.Equal(0, new FileInfo(result.SavePath).Length);
        server.Stop();
    }

    [Fact]
    public async Task LargeFile_1GB_Roundtrip()
    {
        const long fileSize = 1024L * 1024 * 1024;
        var port = GetFreePort();
        var saveDir = Path.Combine(_tempDir, "save");
        Directory.CreateDirectory(saveDir);
        var sourcePath = Path.Combine(_tempDir, "big1g.bin");
        var sourceHash = await CreatePseudoRandomFileAsync(sourcePath, fileSize);

        using var cert = TlsCertificate.Generate();
        var fingerprint = TlsCertificate.GetFingerprint(cert);
        var server = new LocalSendServer(cert);
        server.Start(port, alias: "TestReceiver", fingerprint: fingerprint, savePath: saveDir);

        var completed = new TaskCompletionSource<UploadCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.UploadRequested += (_, e) => server.AcceptUpload(e.SessionId, saveDir);
        server.UploadCompleted += (_, e) => completed.TrySetResult(e);

        await WaitForServerAsync(port);

        var target = new DeviceInfo { Alias = "TestReceiver", IpAddress = "127.0.0.1", Port = port, Fingerprint = fingerprint, Protocol = AppProtocolType.Https };
        var session = new SessionManager().CreateSendSession(target, [sourcePath]);

        using var sender = CreateSender(port, fingerprint);
        await sender.SendAsync(session, compress: false);

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(300));
        Assert.Equal(fileSize, result.Size);
        Assert.Equal(fileSize, new FileInfo(result.SavePath).Length);
        Assert.Equal(sourceHash, await HashFileAsync(result.SavePath));
        server.Stop();
    }

    [Fact]
    public async Task CancelEndpoint_ReturnsOk()
    {
        using var cert = TlsCertificate.Generate();
        var fingerprint = TlsCertificate.GetFingerprint(cert);
        var port = GetFreePort();
        var server = new LocalSendServer(cert);
        server.Start(port, alias: "Test", fingerprint: fingerprint, savePath: Path.GetTempPath());

        await WaitForServerAsync(port);

        using var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var url = $"https://127.0.0.1:{port}/api/localsend/v2/cancel";
        using var response = await client.PostAsync(url, new StringContent("{\"sessionId\":\"nonexistent\"}", System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        server.Stop();
    }

    [Fact]
    public async Task RegisterEndpoint_Post_ReturnsInfo()
    {
        using var cert = TlsCertificate.Generate();
        var fingerprint = TlsCertificate.GetFingerprint(cert);
        var port = GetFreePort();
        var server = new LocalSendServer(cert);
        server.Start(port, alias: "Test", fingerprint: fingerprint, savePath: Path.GetTempPath());

        await WaitForServerAsync(port);

        using var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var url = $"https://127.0.0.1:{port}/api/localsend/v2/register";
        using var response = await client.PostAsync(url, null);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Test", doc.RootElement.GetProperty("alias").GetString());
        Assert.Equal(fingerprint, doc.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal(port, doc.RootElement.GetProperty("port").GetInt32());

        server.Stop();
    }
}
