using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EasyShare.Core.Discovery;

namespace EasyShare.Core.Crypto;

public static class TlsCertificate
{
    private static readonly string CertPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyShare", "cert.pfx");

    private static readonly string PasswordPath = CertPath + ".pwd";

    public static X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(CertPath))
        {
            try
            {
                var password = LoadPassword();
                return X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(CertPath), password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            catch { }
        }

        var cert = Generate();
        try
        {
            var dir = Path.GetDirectoryName(CertPath)!;
            Directory.CreateDirectory(dir);
            var password = LoadPassword() ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx, password));
            File.WriteAllText(PasswordPath, password);
        }
        catch { }
        return cert;
    }

    private static string? LoadPassword()
    {
        if (!File.Exists(PasswordPath)) return null;
        try
        {
            var password = File.ReadAllText(PasswordPath).Trim();
            return password.Length > 0 ? password : null;
        }
        catch
        {
            return null;
        }
    }

    public static X509Certificate2 Generate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=EasyShare"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var ips = MulticastDiscovery.GetLocalIpv4Addresses();
        if (ips.Count == 0) ips.Add(IPAddress.Loopback);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("EasyShare");
        foreach (var ip in ips) sanBuilder.AddIpAddress(ip);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore.AddYears(10);
        var serial = RandomNumberGenerator.GetBytes(16);

        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    public static string GetFingerprint(X509Certificate2 cert)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(cert.RawData);
        return Convert.ToHexStringLower(hash);
    }
}
