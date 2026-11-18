using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace HomeRms.Shared.Security;

/// <summary>
/// Generates and persists a unique, machine/user-bound ECDSA P-256 client identity
/// for this Controller instance (protected via Windows DPAPI) and manages the Agent's
/// pinned-controller allowlist so ONLY your paired Controller can ever connect.
/// </summary>
public static class ControllerIdentity
{
    private static readonly object IdentityLock = new();
    private static X509Certificate2? _cachedControllerCert;

    public static string SecurityDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomeRMS", "Security");

    public static X509Certificate2 GetOrCreateUniqueControllerCert()
    {
        lock (IdentityLock)
        {
            if (_cachedControllerCert is not null)
                return _cachedControllerCert;

            Directory.CreateDirectory(SecurityDir);
            var pfxPath = Path.Combine(SecurityDir, "unique-controller.pfx");
            var keyPath = Path.Combine(SecurityDir, "unique-controller.key");

            if (File.Exists(pfxPath) && File.Exists(keyPath))
            {
                try
                {
                    var pw = UnprotectSecret(File.ReadAllBytes(keyPath));
                    _cachedControllerCert = X509CertificateLoader.LoadPkcs12FromFile(
                        pfxPath, pw, X509KeyStorageFlags.Exportable);
                    return _cachedControllerCert;
                }
                catch
                {
                    // Regenerate if corrupted or moved from another user/machine (DPAPI mismatch)
                }
            }

            var secretBytes = RandomNumberGenerator.GetBytes(32);
            var secret = Convert.ToBase64String(secretBytes);
            File.WriteAllBytes(keyPath, ProtectSecret(secret));

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest(
                $"CN=HomeRMS-Controller-{Environment.MachineName}",
                ecdsa,
                HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(DevCertificates.ClientAuthOid) }, true));

            var now = DateTimeOffset.UtcNow;
            using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
            File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, secret));

            _cachedControllerCert = X509CertificateLoader.LoadPkcs12FromFile(
                pfxPath, secret, X509KeyStorageFlags.Exportable);
            return _cachedControllerCert;
        }
    }

    public static string GetSha256Fingerprint(X509Certificate2 cert)
    {
        var hex = Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));
        return string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }

    public static string NormalizeFingerprint(string fp) =>
        new string(fp.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    public static bool HasClientAuthEku(X509Certificate2 cert) =>
        cert.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(ext => ext.EnhancedKeyUsages.Cast<Oid>())
            .Any(oid => oid.Value == DevCertificates.ClientAuthOid);

    private static byte[] ProtectSecret(string secret)
    {
        var raw = Encoding.UTF8.GetBytes(secret);
        return OperatingSystem.IsWindows()
            ? ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser)
            : raw;
    }

    private static string UnprotectSecret(byte[] blob)
    {
        var raw = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser)
            : blob;
        return Encoding.UTF8.GetString(raw);
    }
}

public static class TrustedControllerStore
{
    public sealed record PinnedEntry(string Sha256, string Label, DateTimeOffset PinnedAt);

    private static readonly object StoreLock = new();

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HomeRMS", "trusted-controllers.json");

    public static List<PinnedEntry> Load(string? path = null)
    {
        path ??= DefaultStorePath;
        lock (StoreLock)
        {
            if (!File.Exists(path)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<PinnedEntry>>(File.ReadAllText(path)) ?? [];
            }
            catch { return []; }
        }
    }

    public static bool HasPinned(string? path = null) => Load(path).Count > 0;

    public static bool IsPinned(X509Certificate2 cert, string? path = null)
    {
        var fp = ControllerIdentity.NormalizeFingerprint(ControllerIdentity.GetSha256Fingerprint(cert));
        return Load(path).Any(e => string.Equals(
            ControllerIdentity.NormalizeFingerprint(e.Sha256), fp, StringComparison.OrdinalIgnoreCase));
    }

    public static void AddPinned(string fingerprint, string label, string? path = null)
    {
        path ??= DefaultStorePath;
        var norm = ControllerIdentity.NormalizeFingerprint(fingerprint);
        if (norm.Length != 64)
            throw new ArgumentException("SHA-256 fingerprint must be 64 hex characters.", nameof(fingerprint));

        lock (StoreLock)
        {
            var list = Load(path);
            if (list.All(e => !string.Equals(
                ControllerIdentity.NormalizeFingerprint(e.Sha256), norm, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new PinnedEntry(norm, label, DateTimeOffset.UtcNow));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    public static void ClearAll(string? path = null)
    {
        path ??= DefaultStorePath;
        lock (StoreLock)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public static bool ValidateAgentPeer(X509Certificate? rawCert, X509Certificate2 caCert, string? storePath = null)
    {
        if (rawCert is null) return false;
        var peer = rawCert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(rawCert.Export(X509ContentType.Cert));

        if (!ControllerIdentity.HasClientAuthEku(peer))
            return false;

        var pinned = Load(storePath);
        if (pinned.Count > 0)
        {
            // Strict Pinning Mode: ONLY pinned Controller fingerprints are accepted!
            // Even certificates signed by the public deployment CA are rejected.
            var norm = ControllerIdentity.NormalizeFingerprint(ControllerIdentity.GetSha256Fingerprint(peer));
            return pinned.Any(e => string.Equals(
                ControllerIdentity.NormalizeFingerprint(e.Sha256), norm, StringComparison.OrdinalIgnoreCase));
        }

        // Unpinned initial state: accept either CA-signed cert or unique self-signed ClientAuth cert
        // (paired/pinned on first consent in RemoteManagementService or via --pin CLI).
        return DevCertificates.ValidateAgainstCa(peer, caCert, DevCertificates.ClientAuthOid)
            || peer.Verify() || peer.Subject.StartsWith("CN=HomeRMS-Controller-", StringComparison.Ordinal);
    }
}
