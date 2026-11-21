using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HomeRms.Shared;
using HomeRms.Shared.Network;
using HomeRms.Shared.Security;

/// <summary>Signed agent announces: what the controller accepts and what it must refuse (no network).</summary>
internal static class AnnounceChecks
{
    public static void Run(Action<bool, string> check)
    {
        DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword);
        var ca = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);
        var agent = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.AgentFileName, RmsPaths.CertPassword);
        var agentPublic = X509CertificateLoader.LoadCertificate(agent.RawData);
        var thumb = AgentAnnounce.Thumbprint(agent);
        var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        IPAddress[] addrs = [IPAddress.Parse("192.168.1.140"), IPAddress.Parse("100.101.102.103"), IPAddress.Parse("fd7a:115c:a1e0::5")];

        AnnounceVerifier Verifier(Func<string, X509Certificate2?>? lookup = null, DateTime? clock = null) =>
            new(ca, lookup ?? (t => t == thumb ? agentPublic : null), () => clock ?? now);

        // valid (RSA agent certificate: thumbprint only, verified with the certificate seen on TLS)
        var packet = AgentAnnounce.Build(agent, "LAPTOP-TEST", 7443, addrs, now);
        var v = Verifier();
        var r = v.Verify(packet);
        check(r.Ok && r.Payload is { MachineName: "LAPTOP-TEST", Port: 7443 } && r.Payload.Thumbprint == thumb && r.Payload.Addresses.Count == 3,
            $"valid announce verifies ({r.Reason})");
        check(packet.Length <= AgentAnnounce.MaxPacketBytes, $"packet stays under {AgentAnnounce.MaxPacketBytes} bytes ({packet.Length})");
        var many = Enumerable.Range(1, 20).Select(i => IPAddress.Parse($"fd00:1234:5678:9abc:def0:1234:5678:{i:x4}")).ToList();
        var big = AgentAnnounce.Build(agent, "LAPTOP-TEST", 7443, many, now);
        check(big.Length <= AgentAnnounce.MaxPacketBytes && Verifier().Verify(big).Ok, $"many long addresses are trimmed to fit ({big.Length} bytes)");

        // replay
        r = v.Verify(packet);
        check(!r.Ok && r.Reason.Contains("replay"), $"replayed packet rejected ({r.Reason})");

        // timestamps
        r = Verifier(clock: now.AddMinutes(3)).Verify(packet);
        check(!r.Ok && r.Reason.Contains("timestamp"), $"expired announce (3 min old) rejected ({r.Reason})");
        r = Verifier(clock: now.AddMinutes(-3)).Verify(packet);
        check(!r.Ok && r.Reason.Contains("timestamp"), $"announce from 3 min in the future rejected ({r.Reason})");
        check(Verifier(clock: now.AddSeconds(100)).Verify(packet).Ok, "100 s clock skew is tolerated");

        // tampered payload: change an address, keep the signature
        var text = Encoding.UTF8.GetString(packet);
        var tampered = Encoding.UTF8.GetBytes(text.Replace("192.168.1.140", "192.168.1.66"));
        r = Verifier().Verify(tampered);
        check(!r.Ok && r.Reason.Contains("signature"), $"tampered address rejected ({r.Reason})");
        var renamed = Encoding.UTF8.GetBytes(text.Replace("|LAPTOP-TEST|", "|EVIL-PC|"));
        check(!Verifier().Verify(renamed).Ok, "tampered machine name rejected");

        // wrong thumbprint: the certificate found does not hash to the announced thumbprint
        using var foreignCa = CreateCa();
        using var foreignLeaf = CreateLeaf(foreignCa, ecdsa: false);
        r = Verifier(lookup: _ => X509CertificateLoader.LoadCertificate(foreignLeaf.RawData)).Verify(AgentAnnounce.Build(agent, "LAPTOP-TEST", 7443, addrs, now));
        check(!r.Ok && r.Reason.Contains("thumbprint"), $"certificate / thumbprint mismatch rejected ({r.Reason})");

        // unknown certificate (never seen on TLS)
        r = Verifier(lookup: _ => null).Verify(AgentAnnounce.Build(agent, "LAPTOP-TEST", 7443, addrs, now));
        check(!r.Ok && r.Reason.Contains("unknown"), $"thumbprint of a certificate never seen rejected ({r.Reason})");

        // unknown CA: a well-formed, correctly signed announce from a certificate of another CA
        var foreignPacket = AgentAnnounce.Build(foreignLeaf, "LAPTOP-TEST", 7443, addrs, now);
        var foreignThumb = AgentAnnounce.Thumbprint(foreignLeaf);
        r = Verifier(lookup: t => t == foreignThumb ? X509CertificateLoader.LoadCertificate(foreignLeaf.RawData) : null).Verify(foreignPacket);
        check(!r.Ok && r.Reason.Contains("CA"), $"certificate from an unknown CA rejected ({r.Reason})");
        using var foreignEc = CreateLeaf(foreignCa, ecdsa: true);
        var embedded = AgentAnnounce.Build(foreignEc, "LAPTOP-TEST", 7443, addrs, now);
        r = Verifier(lookup: _ => null).Verify(embedded);
        check(Encoding.UTF8.GetString(embedded).Split('|')[7].Length > 0 && !r.Ok && r.Reason.Contains("CA"),
            $"small certificate is embedded ({embedded.Length} bytes); an embedded certificate from an unknown CA is rejected ({r.Reason})");

        // ECDSA agent certificate from the Home RMS CA: small enough to embed, verifies without a lookup
        X509Certificate2? homeCa = null;
        try { homeCa = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.CaFileName, RmsPaths.CertPassword); }
        catch (CryptographicException) { }
        if (homeCa?.HasPrivateKey == true)
        {
            using var ecLeaf = CreateLeaf(homeCa, ecdsa: true);
            // Signed by the RSA-4096 Home RMS CA the certificate is too large to embed: thumbprint only.
            var ecPacket = AgentAnnounce.Build(ecLeaf, "LAPTOP-TEST", 7443, addrs, now);
            var ecPublic = X509CertificateLoader.LoadCertificate(ecLeaf.RawData);
            var ecThumb = AgentAnnounce.Thumbprint(ecLeaf);
            r = Verifier(lookup: t => t == ecThumb ? ecPublic : null).Verify(ecPacket);
            check(r.Ok && ecPacket.Length <= AgentAnnounce.MaxPacketBytes,
                $"ECDSA agent certificate: signature verifies, packet {ecPacket.Length} bytes ({r.Reason})");
            check(!Verifier(lookup: _ => null).Verify(ecPacket).Ok, "a certificate too large to embed is not trusted without the one seen on TLS");
        }
        else
        {
            Console.WriteLine("  [skip] ECDSA embedded-certificate case (CA private key not available)");
        }

        // malformed input
        foreach (var junk in new[] { "", "HOMERMS_ANNOUNCE_V1|x", "HOMERMS_AGENT_V1|PC|7443", new string('A', 1400) })
            check(!Verifier().Verify(Encoding.UTF8.GetBytes(junk)).Ok, $"malformed packet rejected ('{(junk.Length > 30 ? junk[..30] + "…" : junk)}')");
    }

    private static X509Certificate2 CreateCa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Foreign CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, bool ecdsa)
    {
        var eku = new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(DevCertificates.ServerAuthOid) }, false);
        var serial = RandomNumberGenerator.GetBytes(12);
        if (ecdsa)
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=homerms-agent-ec", ec, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(eku);
            using var cert = req.Create(issuer.SubjectName, X509SignatureGenerator.CreateForRSA(issuer.GetRSAPrivateKey()!, RSASignaturePadding.Pkcs1),
                DateTimeOffset.UtcNow.AddDays(-1), issuer.NotAfter.AddSeconds(-1), serial);
            return cert.CopyWithPrivateKey(ec);
        }
        using var rsa = RSA.Create(2048);
        var rreq = new CertificateRequest("CN=homerms-agent-foreign", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rreq.CertificateExtensions.Add(eku);
        using var rcert = rreq.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), issuer.NotAfter.AddSeconds(-1), serial);
        return rcert.CopyWithPrivateKey(rsa);
    }
}
