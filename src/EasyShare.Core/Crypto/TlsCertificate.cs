using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EasyShare.Core.Crypto;

public static class TlsCertificate
{
    private static readonly string CertPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyShare", "cert.pfx");

    public static X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(CertPath))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(CertPath), string.Empty,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            catch { }
        }

        var cert = Generate();
        try
        {
            var dir = Path.GetDirectoryName(CertPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx));
        }
        catch { }
        return cert;
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

        var notBefore = DateTimeOffset.UtcNow;
        var notAfter = notBefore.AddYears(10);
        var serial = RandomNumberGenerator.GetBytes(16);

        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx),
            string.Empty,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    public static string GetFingerprint(X509Certificate2 cert)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(cert.RawData);
        return Convert.ToHexStringLower(hash);
    }
}
