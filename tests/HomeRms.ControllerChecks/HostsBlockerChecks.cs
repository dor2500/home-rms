using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using HomeRms.Shared.Hosts;
using HomeRms.Shared.Protocol;

namespace HomeRms.ControllerChecks;

internal static class HostsBlockerChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "homerms-hosts-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var hostsPath = Path.Combine(dir, "hosts");
            var cfgDir = Path.Combine(dir, "cfg");
            File.WriteAllText(hostsPath, "# Custom header\r\n127.0.0.1 localhost\r\n", Encoding.UTF8);

            using var mgr = new HostsManager(new HostsBlockerOptions
            {
                HostsPath = hostsPath,
                ConfigDir = cfgDir,
                RequireElevation = false,
                DnsFlush = () => ["DNS cache flushed (mock)"],
                HttpPort = 0, // disable bind in unit check or use ephemeral
                HttpsPort = 0,
            }, _ => { });

            var cfg0 = mgr.GetConfig();
            check(!cfg0.ListenerEnabled, "hosts blocker: listener is OFF by default");
            check(cfg0.Domains.Count == 0, "hosts blocker: initial blocklist empty");

            var req = new HostsBlocklistRequest { IncludeWww = true };
            req.Domains.Add("facebook.com");
            req.Domains.Add("tiktok.com");
            var res = await mgr.SetBlocklistAsync(req, "test");
            check(res.Success && res.Changed, "hosts blocker: SetBlocklistAsync succeeds");
            check(File.Exists(hostsPath + ".bak"), "hosts blocker: .bak backup created");

            var text = File.ReadAllText(hostsPath);
            check(text.Contains("127.0.0.1 localhost"), "hosts blocker: existing entries preserved");
            check(text.Contains(HostsFile.BeginMarker) && text.Contains(HostsFile.EndMarker), "hosts blocker: markers present");
            check(text.Contains("0.0.0.0 facebook.com") && text.Contains("0.0.0.0 www.facebook.com"), "hosts blocker: maps to 0.0.0.0 with www when listener OFF");

            // Raw save conflict check
            var stale = await mgr.SetRawAsync(new HostsRawRequest { Text = text + "\r\n", ExpectedHash = "badhash" }, "test");
            check(!stale.Success && stale.Conflict, "hosts blocker: SetHostsRaw detects stale expected_hash");

            // Sinkhole HTTP Host & TLS ClientHello SNI parsing
            var httpReq = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: www.facebook.com:80\r\n\r\n");
            var parsedHost = SinkholeProtocol.TryParseHttpHost(httpReq, final: true, out var complete);
            check(complete && parsedHost == "www.facebook.com", "sinkhole: parses HTTP Host header");

            var throttle = new BlockedEventThrottle { PerDomainWindow = TimeSpan.FromSeconds(30) };
            check(throttle.ShouldEmit("pc1", "facebook.com"), "throttle: first hit emits");
            check(!throttle.ShouldEmit("pc1", "facebook.com"), "throttle: duplicate hit within window suppressed");

            // TOTP replay protection
            var secret = HomeRms.Shared.Security.TotpHelper.GenerateSecretBase32();
            var totp = HomeRms.Shared.Security.TotpHelper.ComputeCode(secret);
            check(HomeRms.Shared.Security.TotpHelper.VerifyCode(secret, totp), "totp: first verification succeeds");
            check(!HomeRms.Shared.Security.TotpHelper.VerifyCode(secret, totp), "totp: replay within same step is rejected");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
