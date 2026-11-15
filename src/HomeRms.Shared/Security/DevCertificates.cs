using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HomeRms.Shared.Security;

/// <summary>
/// Creates, embeds, and loads the mutual-TLS trust material for a Home RMS deployment:
/// a private root CA plus one server certificate (the agent) and one client
/// certificate (the controller), both chained to that CA. Both sides validate
/// the peer against the CA, so only certificates from this deployment are accepted.
///
/// On first run on any end-user machine, the embedded deployment certificates are
/// automatically extracted into <c>%ProgramData%\HomeRMS\certs</c> (replacing any
/// mismatched/stale self-generated certificates) so single-file Agent/Controller
/// executables work out-of-the-box across the LAN with zero manual file copying.
/// </summary>
public static class DevCertificates
{
    public const string CaFileName = "homerms-ca.pfx";
    public const string AgentFileName = "homerms-agent.pfx";
    public const string ControllerFileName = "homerms-controller.pfx";

    private static readonly string[] AllFiles = [CaFileName, AgentFileName, ControllerFileName];

    /// <summary>
    /// Ensures the deployment certificates exist in <paramref name="outputDir"/>.
    /// If embedded deployment certificates are bundled in the assembly, they are
    /// automatically synced to <paramref name="outputDir"/> whenever missing or
    /// whenever the on-disk CA thumbprint does not match the bundled CA.
    /// </summary>
    public static void EnsureDeploymentCertificates(string outputDir, string password)
    {
        try { Directory.CreateDirectory(outputDir); }
        catch { return; }

        var caPath = Path.Combine(outputDir, CaFileName);

        // 1. If the assembly carries embedded deployment certificates, sync them on first run
        //    or whenever the local machine has a mismatched CA from an earlier run.
        if (TryGetEmbeddedBytes(CaFileName) is { } embeddedCaBytes)
        {
            var needsSync = !AllFiles.All(f => File.Exists(Path.Combine(outputDir, f)));
            if (!needsSync)
            {
                try
                {
                    using var embeddedCa = X509CertificateLoader.LoadPkcs12(embeddedCaBytes, password);
                    using var diskCa = X509CertificateLoader.LoadPkcs12FromFile(caPath, password);
                    if (!string.Equals(embeddedCa.Thumbprint, diskCa.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        needsSync = true;
                }
                catch
                {
                    needsSync = true;
                }
            }

            if (needsSync)
            {
                foreach (var file in AllFiles)
                {
                    if (TryGetEmbeddedBytes(file) is { } bytes)
                    {
                        try { File.WriteAllBytes(Path.Combine(outputDir, file), bytes); }
                        catch { /* If locked/unwritable, Load() will fall back to embedded bytes */ }
                    }
                }
            }
            return;
        }

        // 2. Fallback: generate fresh deployment certificates if no embedded certs exist.
        if (File.Exists(caPath))
            return;

        GenerateNewCertificates(outputDir, password);
    }

    /// <summary>Generates a fresh set of CA, Agent, and Controller PFX files into <paramref name="outputDir"/>.</summary>
    public static void GenerateNewCertificates(string outputDir, string password)
    {
        Directory.CreateDirectory(outputDir);

        using var ca = CreateCa("HomeRMS Local CA");
        File.WriteAllBytes(Path.Combine(outputDir, CaFileName), ca.Export(X509ContentType.Pfx, password));

        using var agent = CreateLeaf(ca, "homerms-agent", isServer: true);
        File.WriteAllBytes(Path.Combine(outputDir, AgentFileName), agent.Export(X509ContentType.Pfx, password));

        using var controller = CreateLeaf(ca, "homerms-controller", isServer: false);
        File.WriteAllBytes(Path.Combine(outputDir, ControllerFileName), controller.Export(X509ContentType.Pfx, password));
    }

    public static X509Certificate2 Load(string dir, string fileName, string password)
    {
        EnsureDeploymentCertificates(dir, password);

        var storageFlags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet
            : X509KeyStorageFlags.Exportable;

        // Prefer the embedded deployment certificate if available and the disk copy is mismatched
        if (TryGetEmbeddedBytes(fileName) is { } embeddedBytes &&
            TryGetEmbeddedBytes(CaFileName) is { } embeddedCaBytes)
        {
            try
            {
                var diskPath = Path.Combine(dir, fileName);
                var diskCaPath = Path.Combine(dir, CaFileName);
                if (File.Exists(diskPath) && File.Exists(diskCaPath))
                {
                    using var embeddedCa = X509CertificateLoader.LoadPkcs12(embeddedCaBytes, password);
                    using var diskCa = X509CertificateLoader.LoadPkcs12FromFile(diskCaPath, password);
                    if (string.Equals(embeddedCa.Thumbprint, diskCa.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        return X509CertificateLoader.LoadPkcs12FromFile(diskPath, password, storageFlags);
                    }
                }
            }
            catch
            {
                // Fall through to loading directly from embedded bytes via user temp/local storage
            }

            // Write to per-user LocalApplicationData if primary directory was locked or unwritable
            var fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HomeRMS", "certs");
            Directory.CreateDirectory(fallbackDir);
            var fallbackPath = Path.Combine(fallbackDir, fileName);
            File.WriteAllBytes(fallbackPath, embeddedBytes);
            return X509CertificateLoader.LoadPkcs12FromFile(fallbackPath, password, storageFlags);
        }

        // NOTE: do not use EphemeralKeySet here. On Windows, SChannel cannot use an
        // ephemeral private key for the Kestrel SERVER certificate, which aborts the
        // TLS handshake. A persisted user key set works for both server and client.
        return X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(dir, fileName),
            password,
            storageFlags);
    }

    /// <summary>Returns the CA certificate with the private key stripped, for peer validation.</summary>
    public static X509Certificate2 LoadCaPublicOnly(string dir, string password)
    {
        using var full = Load(dir, CaFileName, password);
        return X509CertificateLoader.LoadCertificate(full.Export(X509ContentType.Cert));
    }

    /// <summary>Validates that <paramref name="peerCert"/> chains to <paramref name="ca"/>.</summary>
    public static bool ValidateAgainstCa(X509Certificate2 peerCert, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        // Tolerate clock differences between home LAN PCs while strictly enforcing cryptographic signature
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;

        if (!chain.Build(peerCert))
            return false;

        var root = chain.ChainElements[^1].Certificate;
        return string.Equals(root.Thumbprint, ca.Thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? TryGetEmbeddedBytes(string fileName)
    {
        var asm = typeof(DevCertificates).Assembly;
        using var stream = asm.GetManifestResourceStream($"HomeRms.Shared.Certs.{fileName}");
        if (stream is null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
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
        // Backdate by 5 years so machines with different system clocks never reject on NotBefore
        return req.CreateSelfSigned(now.AddYears(-5), now.AddYears(10));
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
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("homerms-agent");
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            req.CertificateExtensions.Add(san.Build());
        }

        var now = DateTimeOffset.UtcNow;
        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        using var issued = req.Create(ca, now.AddYears(-5), now.AddYears(9), serial);
        return issued.CopyWithPrivateKey(rsa);
    }
}
