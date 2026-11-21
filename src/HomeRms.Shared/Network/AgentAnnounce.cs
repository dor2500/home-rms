using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HomeRms.Shared.Security;

namespace HomeRms.Shared.Network;

/// <summary>The signed content of an agent announce.</summary>
public sealed record AnnouncePayload(
    string MachineName, int Port, IReadOnlyList<IPAddress> Addresses, long TimestampUnixMs, string Nonce, string Thumbprint)
{
    public DateTime TimestampUtc => DateTimeOffset.FromUnixTimeMilliseconds(TimestampUnixMs).UtcDateTime;
}

/// <summary>Outcome of <see cref="AnnounceVerifier.Verify"/>. <see cref="Payload"/> is only trustworthy when <see cref="Ok"/>.</summary>
public sealed record AnnounceResult(bool Ok, string Reason, AnnouncePayload? Payload, X509Certificate2? Certificate)
{
    public static AnnounceResult Fail(string reason, AnnouncePayload? p = null) => new(false, reason, p, null);
}

/// <summary>
/// "Call home" announce an agent sends (unicast UDP) to the controllers that connected to it, so a
/// controller learns the agent's new addresses even across subnets where broadcast discovery cannot reach.
/// <para>Wire format (UTF-8, one line):
/// <c>HOMERMS_ANNOUNCE_V1|name|port|ip1,ip2|unixMs|nonceHex|sha256ThumbHex|certDerBase64OrEmpty|signatureBase64</c>.
/// The signature covers everything before the last '|' and is made with the agent's TLS server
/// certificate key (RSA PKCS#1 v1.5 / ECDSA P1363, SHA-256).</para>
/// </summary>
public static class AgentAnnounce
{
    public const string Prefix = "HOMERMS_ANNOUNCE_V1";
    public const int MaxPacketBytes = 1200;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>SHA-256 over the DER certificate, upper-case hex without separators.</summary>
    public static string Thumbprint(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    /// <summary>
    /// Builds a signed announce. The certificate itself is embedded only when the packet stays under
    /// <see cref="MaxPacketBytes"/> (ECDSA keys); otherwise only its thumbprint is sent and the controller
    /// verifies with the certificate it saw on an earlier TLS connection.
    /// </summary>
    public static byte[] Build(X509Certificate2 certWithKey, string machineName, int port, IEnumerable<IPAddress> addresses,
        DateTime utcNow, bool includeCertIfFits = true, string? nonce = null)
    {
        nonce ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var thumb = Thumbprint(certWithKey);
        var ts = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var addrs = addresses.Take(RmsNetwork.MaxAddresses).ToList();
        var certB64 = includeCertIfFits ? Convert.ToBase64String(certWithKey.RawData) : "";

        while (true)
        {
            var signed = $"{Prefix}|{machineName}|{port}|{string.Join(",", addrs)}|{ts}|{nonce}|{thumb}|{certB64}";
            var packet = signed + "|" + Convert.ToBase64String(Sign(certWithKey, Encoding.UTF8.GetBytes(signed)));
            var bytes = Encoding.UTF8.GetBytes(packet);
            if (bytes.Length <= MaxPacketBytes) return bytes;
            if (certB64.Length > 0) { certB64 = ""; continue; }
            if (addrs.Count > 1) { addrs.RemoveAt(addrs.Count - 1); continue; }
            return bytes; // a single address and no certificate: cannot get smaller
        }
    }

    private static byte[] Sign(X509Certificate2 cert, byte[] data)
    {
        using var rsa = cert.GetRSAPrivateKey();
        if (rsa is not null) return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ec = cert.GetECDsaPrivateKey();
        if (ec is not null) return ec.SignData(data, HashAlgorithmName.SHA256);
        throw new CryptographicException("The agent certificate has no usable RSA or ECDSA private key.");
    }

    internal static bool VerifySignature(X509Certificate2 cert, byte[] data, byte[] signature)
    {
        try
        {
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is not null) return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ec = cert.GetECDsaPublicKey();
            if (ec is not null) return ec.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { }
        return false;
    }

    internal sealed record Parsed(AnnouncePayload Payload, byte[]? CertDer, byte[] SignedBytes, byte[] Signature);

    internal static Parsed? Parse(ReadOnlySpan<byte> packet, out string reason)
    {
        reason = "";
        if (packet.Length is 0 or > 1500) { reason = "bad size"; return null; }
        string text;
        try { text = new UTF8Encoding(false, true).GetString(packet); }
        catch (DecoderFallbackException) { reason = "not UTF-8"; return null; }

        var parts = text.Split('|');
        if (parts.Length != 9 || parts[0] != Prefix) { reason = "not an announce"; return null; }
        var name = parts[1];
        if (!RmsNetwork.IsPlausibleMachineName(name)) { reason = "bad machine name"; return null; }
        if (!int.TryParse(parts[2], out var port) || port is < 1 or > 65535) { reason = "bad port"; return null; }
        if (!long.TryParse(parts[4], out var ts)) { reason = "bad timestamp"; return null; }
        var nonce = parts[5];
        if (nonce.Length is < 16 or > 64 || !nonce.All(Uri.IsHexDigit)) { reason = "bad nonce"; return null; }
        var thumb = parts[6];
        if (thumb.Length != 64 || !thumb.All(Uri.IsHexDigit)) { reason = "bad thumbprint"; return null; }

        byte[]? certDer = null;
        byte[] sig;
        try
        {
            if (parts[7].Length > 0) certDer = Convert.FromBase64String(parts[7]);
            sig = Convert.FromBase64String(parts[8]);
        }
        catch (FormatException) { reason = "bad base64"; return null; }

        var signedText = text[..text.LastIndexOf('|')];
        var payload = new AnnouncePayload(name, port, RmsNetwork.ParseAddressList(parts[3]), ts, nonce.ToUpperInvariant(), thumb.ToUpperInvariant());
        return new Parsed(payload, certDer, Encoding.UTF8.GetBytes(signedText), sig);
    }
}

/// <summary>
/// Verifies announces: the certificate (embedded, or looked up by thumbprint among certificates the
/// controller already saw on TLS connections) must chain to the deployment CA with serverAuth (the same
/// rule as <c>RmsConnection</c>), the signature must verify, the timestamp must be within ±2 minutes and
/// the nonce must not have been seen before. Whether the announce may change a given endpoint (its
/// pinned certificate thumbprint) is decided by the caller.
/// </summary>
public sealed class AnnounceVerifier(X509Certificate2 ca, Func<string, X509Certificate2?> certLookup, Func<DateTime>? utcNow = null)
{
    private readonly ConcurrentDictionary<string, DateTime> _seenNonces = new(StringComparer.Ordinal);
    private int _calls;

    public AnnounceResult Verify(ReadOnlySpan<byte> packet)
    {
        var parsed = AgentAnnounce.Parse(packet, out var parseError);
        if (parsed is null) return AnnounceResult.Fail(parseError);
        var p = parsed.Payload;

        X509Certificate2? cert;
        if (parsed.CertDer is not null)
        {
            try { cert = X509CertificateLoader.LoadCertificate(parsed.CertDer); }
            catch (CryptographicException) { return AnnounceResult.Fail("embedded certificate is not valid DER", p); }
        }
        else
        {
            cert = certLookup(p.Thumbprint);
            if (cert is null) return AnnounceResult.Fail("unknown agent certificate (no TLS connection seen with it yet)", p);
        }

        if (!string.Equals(AgentAnnounce.Thumbprint(cert), p.Thumbprint, StringComparison.OrdinalIgnoreCase))
            return AnnounceResult.Fail("certificate does not match the announced thumbprint", p);
        if (!DevCertificates.ValidateAgainstCa(cert, ca, DevCertificates.ServerAuthOid))
            return AnnounceResult.Fail("certificate does not chain to the Home RMS CA with serverAuth", p);
        if (!AgentAnnounce.VerifySignature(cert, parsed.SignedBytes, parsed.Signature))
            return AnnounceResult.Fail("signature does not verify", p);

        var now = (utcNow ?? (() => DateTime.UtcNow))();
        var skew = (p.TimestampUtc - now).Duration();
        if (skew > AgentAnnounce.MaxClockSkew)
            return AnnounceResult.Fail($"timestamp is {skew.TotalSeconds:0}s away from this clock", p);

        if (Interlocked.Increment(ref _calls) % 128 == 0 || _seenNonces.Count > 4096) PurgeNonces(now);
        // Remember the nonce for longer than the accepted window so a replay can never become valid again.
        if (!_seenNonces.TryAdd(p.Nonce, now + AgentAnnounce.MaxClockSkew + AgentAnnounce.MaxClockSkew))
            return AnnounceResult.Fail("replayed announce (nonce already seen)", p);

        return new AnnounceResult(true, "ok", p, cert);
    }

    private void PurgeNonces(DateTime now)
    {
        foreach (var (nonce, expiry) in _seenNonces)
            if (expiry < now) _seenNonces.TryRemove(nonce, out _);
    }
}
