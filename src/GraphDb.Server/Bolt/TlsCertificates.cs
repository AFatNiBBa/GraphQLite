using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GraphDb.Server.Bolt;

/// <summary>Loads or creates the server certificate for TLS.</summary>
public static class TlsCertificates
{
    /// <summary>
    /// Loads a certificate with its private key: a PKCS#12 file (<c>.pfx</c> / <c>.p12</c>, optional password) or a PEM
    /// certificate with a PEM key file.
    /// </summary>
    public static X509Certificate2 Load(string certificatePath, string? keyPath = null, string? password = null)
    {
        X509Certificate2 cert;
        if (keyPath != null)
        {
            cert = password is null
                ? X509Certificate2.CreateFromPemFile(certificatePath, keyPath)
                : X509Certificate2.CreateFromEncryptedPemFile(certificatePath, password, keyPath);
        }
        else
        {
            var bytes = File.ReadAllBytes(certificatePath);
            if (X509Certificate2.GetCertContentType(bytes) == X509ContentType.Pkcs12) return X509CertificateLoader.LoadPkcs12(bytes, password);
            cert = X509Certificate2.CreateFromPemFile(certificatePath); // PEM with the key in the same file
        }
        if (!cert.HasPrivateKey) throw new InvalidOperationException($"'{certificatePath}' has no private key");
        // the TLS stack of Windows cannot use keys imported from PEM directly: round-trip through PKCS#12
        using (cert)
            return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>
    /// Loads the self-signed certificate stored at <paramref name="path"/>, creating it the first time (valid for
    /// localhost, the machine name and the loopback addresses, plus <paramref name="extraNames"/>). Clients must be told
    /// to trust it (<c>bolt+ssc://</c>, or accepting it once in the web browser).
    /// </summary>
    public static X509Certificate2 SelfSigned(string path, IEnumerable<string>? extraNames = null)
    {
        if (File.Exists(path))
        {
            var existing = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(path), null);
            if (existing.NotAfter > DateTime.Now.AddDays(1)) return existing;
            existing.Dispose();
        }
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=GraphDb", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var n in extraNames ?? [])
        {
            if (IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip);
            else san.AddDnsName(n);
        }
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
        // browsers refuse certificates valid for more than 398 days
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(397));
        var pfx = cert.Export(X509ContentType.Pkcs12);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }
}
