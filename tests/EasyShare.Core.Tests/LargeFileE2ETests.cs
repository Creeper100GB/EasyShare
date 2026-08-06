using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using EasyShare.Core.Crypto;
using EasyShare.Core.Models;
using EasyShare.Core.Sessions;
using EasyShare.Transport.FileTransfer;
using EasyShare.Transport.Server;
using AppProtocolType = EasyShare.Core.Models.ProtocolType;

namespace EasyShare.Core.Tests;

public class LargeFileE2ETests
{
    [Fact]
    public async Task LargeFile_StreamsOverTls_ReceivesIdenticalBytes()
    {
        const long fileSize = 300L * 1024 * 1024;
        var tempDir = Path.Combine(Path.GetTempPath(), "EasyShareE2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var server = null as LocalSendServer;
        try
        {
            var sourcePath = Path.Combine(tempDir, "big.bin");
            var sourceHash = await CreatePseudoRandomFileAsync(sourcePath, fileSize);

            var saveDir = Path.Combine(tempDir, "received");
            var port = GetFreePort();
            using var cert = TlsCertificate.Generate();
            var fingerprint = TlsCertificate.GetFingerprint(cert);

            server = new LocalSendServer(cert);
            server.Start(port, alias: "TestPc", fingerprint: fingerprint, savePath: saveDir);

            var completed = new TaskCompletionSource<UploadCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.UploadRequested += (_, e) => server.AcceptUpload(e.SessionId, saveDir);
            server.UploadCompleted += (_, e) => completed.TrySetResult(e);

            await WaitForServerAsync(port);

            var target = new DeviceInfo
            {
                Alias = "TestPc",
                IpAddress = "127.0.0.1",
                Port = port,
                Fingerprint = fingerprint,
                Protocol = AppProtocolType.Https,
            };
            var session = new SessionManager().CreateSendSession(target, [sourcePath]);

            using var sender = new FileSender(
                new DeviceAnnouncement
                {
                    Alias = "Sender",
                    Version = "2.0",
                    Fingerprint = fingerprint,
                    Port = 0,
                    Protocol = AppProtocolType.Https,
                },
                "127.0.0.1", port, fingerprint, useTls: true, "/api/localsend/v2");

            await sender.SendAsync(session, compress: false);

            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal("big.bin", result.FileName);
            Assert.False(result.Compressed);
            Assert.Equal(fileSize, result.Size);
            Assert.True(File.Exists(result.SavePath));
            Assert.Equal(fileSize, new FileInfo(result.SavePath).Length);
            Assert.Equal(sourceHash, await HashFileAsync(result.SavePath));
        }
        finally
        {
            server?.Stop();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FolderWithLargeFile_CompressedZip_ExtractsIdentically()
    {
        const long fileSize = 250L * 1024 * 1024;
        var tempDir = Path.Combine(Path.GetTempPath(), "EasyShareE2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var server = null as LocalSendServer;
        try
        {
            var folderPath = Path.Combine(tempDir, "bigfolder");
            Directory.CreateDirectory(folderPath);
            var sourcePath = Path.Combine(folderPath, "data.bin");
            var sourceHash = await CreatePseudoRandomFileAsync(sourcePath, fileSize);

            var saveDir = Path.Combine(tempDir, "received");
            var port = GetFreePort();
            using var cert = TlsCertificate.Generate();
            var fingerprint = TlsCertificate.GetFingerprint(cert);

            server = new LocalSendServer(cert);
            server.Start(port, alias: "TestPc", fingerprint: fingerprint, savePath: saveDir);

            var completed = new TaskCompletionSource<UploadCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.UploadRequested += (_, e) => server.AcceptUpload(e.SessionId, saveDir);
            server.UploadCompleted += (_, e) => completed.TrySetResult(e);

            await WaitForServerAsync(port);

            var target = new DeviceInfo
            {
                Alias = "TestPc",
                IpAddress = "127.0.0.1",
                Port = port,
                Fingerprint = fingerprint,
                Protocol = AppProtocolType.Https,
            };
            var session = new SessionManager().CreateSendSession(target, [folderPath]);
            Assert.True(session.ContainsFolders);
            Assert.Equal("bigfolder", session.ZipName);

            using var sender = new FileSender(
                new DeviceAnnouncement
                {
                    Alias = "Sender",
                    Version = "2.0",
                    Fingerprint = fingerprint,
                    Port = 0,
                    Protocol = AppProtocolType.Https,
                },
                "127.0.0.1", port, fingerprint, useTls: true, "/api/localsend/v2");

            await sender.SendAsync(session, compress: false);

            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(120));
            Assert.Equal("bigfolder.zip", result.FileName);
            Assert.True(result.Compressed);
            Assert.Equal(1, result.OriginalFileCount);
            Assert.True(File.Exists(result.SavePath));

            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(result.SavePath, extractDir);

            var extractedPath = Path.Combine(extractDir, "data.bin");
            Assert.True(File.Exists(extractedPath));
            Assert.Equal(fileSize, new FileInfo(extractedPath).Length);
            Assert.Equal(sourceHash, await HashFileAsync(extractedPath));
        }
        finally
        {
            server?.Stop();
            Directory.Delete(tempDir, recursive: true);
        }
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
            catch
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException("Server did not start listening within 5 seconds.");
    }
}
