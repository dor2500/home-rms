using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HomeRms.Shared.Security;

/// <summary>
/// Creates and loads the mutual-TLS trust material for a Home RMS deployment:
/// a private root CA plus one server certificate (the agent) and one client
/// certificate (the controller), both chained to that CA. Both sides validate
/// the peer against the CA, so only certificates you generated are accepted.
///
/// This replaces the original "pre-shared key embedded at build time" idea with
/// real per-deployment PKI, which is both safer and standard.
/// </summary>
public static class DevCertificates
{
    public const string CaFileName = "homerms-ca.pfx";
    public const string AgentFileName = "homerms-agent.pfx";
    public const string ControllerFileName = "homerms-controller.pfx";

    /// <summary>
    /// Generates the CA + agent + controller certificates into <paramref name="outputDir"/>
    /// if they do not already exist. Safe to call repeatedly.
    /// </summary>
    public static void EnsureDeploymentCertificates(string outputDir, string password)
    {
        Directory.CreateDirectory(outputDir);
        var caPath = Path.Combine(outputDir, CaFileName);
        if (File.Exists(caPath))
            return;

        using var ca = CreateCa("HomeRMS Local CA");
        File.WriteAllBytes(caPath, ca.Export(X509ContentType.Pfx, password));

        using var agent = CreateLeaf(ca, "homerms-agent", isServer: true);
        File.WriteAllBytes(Path.Combine(outputDir, AgentFileName), agent.Export(X509ContentType.Pfx, password));

        using var controller = CreateLeaf(ca, "homerms-controller", isServer: false);
        File.WriteAllBytes(Path.Combine(outputDir, ControllerFileName), controller.Export(X509ContentType.Pfx, password));
    }

    public static X509Certificate2 Load(string dir, string fileName, string password) =>
        // NOTE: do not use EphemeralKeySet here. On Windows, SChannel cannot use an
        // ephemeral private key for the Kestrel SERVER certificate, which aborts the
        // TLS handshake. A persisted user key set works for both server and client.
        X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(dir, fileName),
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

    /// <summary>Returns the CA certificate with the private key stripped, for peer validation.</summary>
    public static X509Certificate2 LoadCaPublicOnly(string dir, string password)
    {
        using var full = Load(dir, CaFileName, password);
        return X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert));
    }

    private static X509Certificate2 CreateCa(string commonName)
    {
        using var rsa = RSA.Create(4096);
        var req = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 ca, string commonName, bool isServer)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={commonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var eku = isServer
            ? new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }  // serverAuth
            : new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }; // clientAuth
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        if (isServer)
        {
            // The agent listens on the LAN; allow connecting by hostname or any IP.
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(Environment.MachineName);
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
        }

        var now = DateTimeOffset.UtcNow;
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var issued = req.Create(ca, now.AddDays(-1), now.AddYears(5), serial);
        return issued.CopyWithPrivateKey(rsa);
    }
}
