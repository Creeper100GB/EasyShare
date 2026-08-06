using System.Security.Cryptography;
using EasyShare.Core.Models;
using EasyShare.Core.Security;
using EasyShare.Core.Services;
using EasyShare.Core.Sessions;
using EasyShare.Transport.Server;

namespace EasyShare.Core.Tests;

public class HardeningTests
{
    [Theory]
    [InlineData("EasyShare.exe", true)]
    [InlineData("EasyShare 2.exe", false)]
    [InlineData("evil.exe;start", false)]
    [InlineData("evil.exe&calc", false)]
    [InlineData("script.bat", false)]
    [InlineData("..\\evil.exe", false)]
    [InlineData("easy share.exe", false)]
    [InlineData("sub.app.exe", true)]
    public void UpdateService_ValidatesExecutableName(string name, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsValidExecutableName(name));
    }

    [Fact]
    public void VerifySha256File_AcceptsMatchingHash()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "EasyShare checksum verification test");
            using var sha = SHA256.Create();
            var expected = Convert.ToHexStringLower(sha.ComputeHash(File.ReadAllBytes(path)));

            Assert.True(UpdateService.VerifySha256File(path, expected));
            Assert.True(UpdateService.VerifySha256File(path, expected + "  EasyShare-1.8.1-win-x64.zip"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VerifySha256File_RejectsMismatchAndGarbage()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "EasyShare checksum verification test");
            Assert.False(UpdateService.VerifySha256File(path, new string('0', 64)));
            Assert.False(UpdateService.VerifySha256File(path, null));
            Assert.False(UpdateService.VerifySha256File(path, string.Empty));
            Assert.False(UpdateService.VerifySha256File(path, "not-a-hash"));
            Assert.False(UpdateService.VerifySha256File(path, new string('a', 63)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetUniquePath_ReturnsRequestedName_WhenAbsent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EasyShareTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = LocalSendServer.GetUniquePath(dir, "test.txt");
            Assert.Equal(Path.Combine(dir, "test.txt"), path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetUniquePath_IncrementsSuffix_OnCollision()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EasyShareTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "test.txt"), "occupied");
            var path = LocalSendServer.GetUniquePath(dir, "test.txt");
            Assert.Equal(Path.Combine(dir, "test (1).txt"), path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TrustStore_IgnoresFingerprintCase()
    {
        var path = Path.Combine(Path.GetTempPath(), "EasyShareTest", Guid.NewGuid().ToString("N"), "trusted.json");
        try
        {
            var store = new TrustStore(path);
            store.AddTrusted("AB12CD34", "alias");
            var reloaded = new TrustStore(path);
            Assert.True(reloaded.IsTrusted("ab12cd34"));
            Assert.Equal("alias", reloaded.GetAlias("AB12CD34"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TrustStore_LoadsLegacyArrayFormat()
    {
        var path = Path.Combine(Path.GetTempPath(), "EasyShareTest", Guid.NewGuid().ToString("N"), "trusted.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "[\"FINGERPRINT-ONE\"]");
            var store = new TrustStore(path);
            Assert.True(store.IsTrusted("fingerprint-one"));
            var reloaded = new TrustStore(path);
            Assert.True(reloaded.IsTrusted("fingerprint-one"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TrustStore_RemovesTrusted()
    {
        var path = Path.Combine(Path.GetTempPath(), "EasyShareTest", Guid.NewGuid().ToString("N"), "trusted.json");
        try
        {
            var store = new TrustStore(path);
            store.AddTrusted("FP", "alias");
            store.RemoveTrusted("fp");
            Assert.False(store.IsTrusted("FP"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CreateSendSession_ExpandsFolderRecursively()
    {
        var root = Path.Combine(Path.GetTempPath(), "EasyShareTest", Guid.NewGuid().ToString("N"), "MyFolder");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "A");
            File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "B");

            var manager = new SessionManager();
            var session = manager.CreateSendSession(new DeviceInfo(), [root]);

            Assert.True(session.ContainsFolders);
            Assert.Equal("MyFolder", session.ZipName);
            Assert.Equal(2, session.Files.Count);
            Assert.Contains(session.Files, f => f.FileName == "a.txt");
            Assert.Contains(session.Files, f => f.FileName == "sub/b.txt");
            Assert.All(session.Files, f => Assert.NotNull(f.LocalFilePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateSendSession_MixedPathsPrefixFolderEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "EasyShareTest", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Folder");
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(root, "f.txt");
            File.WriteAllText(file, "F");
            File.WriteAllText(Path.Combine(folder, "c.txt"), "C");

            var manager = new SessionManager();
            var session = manager.CreateSendSession(new DeviceInfo(), [file, folder]);

            Assert.True(session.ContainsFolders);
            Assert.Null(session.ZipName);
            Assert.Contains(session.Files, f => f.FileName == "Folder/c.txt");
            Assert.Contains(session.Files, f => f.FileName == "f.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateSendSession_EmptyFolderProducesNoEntries()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EasyShareTest", Guid.NewGuid().ToString("N"), "Empty");
        Directory.CreateDirectory(folder);
        try
        {
            var manager = new SessionManager();
            var session = manager.CreateSendSession(new DeviceInfo(), [folder]);

            Assert.True(session.ContainsFolders);
            Assert.Equal("Empty", session.ZipName);
            Assert.Empty(session.Files);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
