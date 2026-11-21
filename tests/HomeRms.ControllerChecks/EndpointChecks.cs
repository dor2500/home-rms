using System.Net;
using System.Text;
using System.Text.Json;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Shared.Network;

/// <summary>Connect-by-computer-name rules: host validation, discovery reply parsing and IP healing (pure, no network).</summary>
internal static class EndpointChecks
{
    public static void Run(Action<bool, string> check)
    {
        // ---- host validation ----
        foreach (var ok in new[] { "LAPTOP-TEST", "laptop-test", "laptop-test.local", "nas.home.example", "pc1", "localhost", "192.168.1.20", "10.0.0.1", "::1", "fe80::1", "laptop-test.local." })
            check(EndpointAddress.IsValidHost(ok, out var e), $"valid host: '{ok}'{(e.Length > 0 ? " -> " + e : "")}");
        foreach (var bad in new[] { "", "  ", "laptop test", "-laptop", "laptop-", "a..b", "192.168.1", "300.1.1.1", "under_score", "pc|x", new string('a', 64), "http://pc", "pc:7443", "1.2.3.4.5" })
            check(!EndpointAddress.IsValidHost(bad, out _), $"rejected host: '{bad}'");
        check(EndpointAddress.IsIpAddress("192.168.1.140") && !EndpointAddress.IsIpAddress("123") && !EndpointAddress.IsIpAddress("LAPTOP-TEST"),
            "IsIpAddress: dotted quad yes, bare number / name no");
        check(EndpointAddress.IsLoopbackHost("localhost") && EndpointAddress.IsLoopbackHost("127.0.0.1") && !EndpointAddress.IsLoopbackHost("192.168.1.2"),
            "loopback hosts");

        // ---- name comparison ----
        check(EndpointAddress.NamesEqual("laptop-test.local", "LAPTOP-TEST") && EndpointAddress.NamesEqual("LAPTOP-TEST.home.lan", "laptop-test"),
            ".local / domain suffix and case are ignored when comparing computer names");
        check(!EndpointAddress.NamesEqual("laptop-test2", "laptop-test") && !EndpointAddress.NamesEqual("", ""), "different / empty names are not equal");
        check(EndpointAddress.DisplayNameStartsWith("DOR-WORKSTATION (Primary Workstation)", "DOR-WORKSTATION"), "display name 'DOR-WORKSTATION (Primary Workstation)' matches DOR-WORKSTATION");
        check(!EndpointAddress.DisplayNameStartsWith("DOR-WORKSTATION-2", "DOR-WORKSTATION") && !EndpointAddress.DisplayNameStartsWith("PC2 desk", "PC"),
            "display-name prefix must end at a word boundary (DOR-WORKSTATION-2, PC2)");

        // ---- subnets ----
        var lan = new LocalSubnet(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0"));
        check(lan.Contains(IPAddress.Parse("192.168.1.140")) && !lan.Contains(IPAddress.Parse("192.168.2.1")), "subnet membership");
        check(lan.Broadcast?.ToString() == "192.168.1.255", $"directed broadcast -> {lan.Broadcast}");
        check(new LocalSubnet(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.255.255")).Broadcast is null, "no directed broadcast for /32");
        var ranked = EndpointAddress.Rank([IPAddress.Parse("fd7a::1"), IPAddress.Parse("100.64.1.2"), IPAddress.Parse("192.168.1.140")], [lan]);
        check(string.Join(",", ranked) == "192.168.1.140,100.64.1.2,fd7a::1", $"ranking: same subnet, other IPv4, IPv6 -> {string.Join(",", ranked)}");

        // ---- discovery replies ----
        var old = RmsNetwork.ParseDiscoveryReply("HOMERMS_AGENT_V1|LAPTOP-TEST|7443");
        check(old is { MachineName: "LAPTOP-TEST", Port: 7443, Addresses.Count: 0 }, "old 3-field reply parses");
        var multi = RmsNetwork.ParseDiscoveryReply("HOMERMS_AGENT_V1|LAPTOP-TEST|7443|192.168.1.140,10.8.0.4,junk,127.0.0.1,192.168.1.140,fd7a:115c::5");
        check(multi is not null && string.Join(",", multi.Addresses) == "192.168.1.140,10.8.0.4,fd7a:115c::5",
            $"multi-address reply: junk, loopback and duplicates dropped -> {(multi is null ? "null" : string.Join(",", multi.Addresses))}");
        check(RmsNetwork.ParseDiscoveryReply("HOMERMS_AGENT_V1|PC|notaport")?.Port == 7443, "bad port falls back to 7443 (as before)");
        check(RmsNetwork.ParseDiscoveryReply("HOMERMS_DISCOVER_V1") is null && RmsNetwork.ParseDiscoveryReply("HOMERMS_AGENT_V1|bad name|7443") is null
              && RmsNetwork.ParseDiscoveryReply("HOMERMS_AGENT_V1||7443") is null && RmsNetwork.ParseDiscoveryReply(null) is null,
            "probes, names with spaces, empty names are not replies");
        var built = RmsNetwork.BuildDiscoveryReply("PC-1", 7443, [IPAddress.Parse("192.168.1.5"), IPAddress.Parse("10.0.0.5")]);
        check(built == "HOMERMS_AGENT_V1|PC-1|7443|192.168.1.5,10.0.0.5" && RmsNetwork.BuildDiscoveryReply("PC-1", 7443, []) == "HOMERMS_AGENT_V1|PC-1|7443",
            "reply builder (4th field only when there are addresses)");
        var oldControllerView = built.Split('|');
        check(oldControllerView.Length >= 3 && oldControllerView[1] == "PC-1" && int.Parse(oldControllerView[2]) == 7443, "old controllers still read name and port from the new reply");

        var pkt = EndpointHealing.ParseReplyPacket(Encoding.UTF8.GetBytes("HOMERMS_AGENT_V1|LAPTOP-TEST|7443|10.8.0.4,192.168.1.140"), IPAddress.Parse("::ffff:192.168.1.140"));
        check(pkt is not null && string.Join(",", pkt.Addresses) == "192.168.1.140,10.8.0.4", $"packet source first, IPv4-mapped normalized, no duplicate -> {(pkt is null ? "null" : string.Join(",", pkt.Addresses))}");
        var merged = EndpointHealing.Merge([
            new AgentSighting("LAPTOP-TEST", 7443, [IPAddress.Parse("10.8.0.4"), IPAddress.Parse("192.168.1.140")], "LAN discovery"),
            new AgentSighting("laptop-test", 7443, [IPAddress.Parse("192.168.1.140"), IPAddress.Parse("10.8.0.4")], "LAN discovery"),
            new AgentSighting("OTHER-PC", 7443, [IPAddress.Parse("192.168.1.50")], "LAN discovery"),
        ], [lan]);
        check(merged.Count == 2 && string.Join(",", merged[0].Addresses) == "192.168.1.140,10.8.0.4", "replies of one agent via two NICs merge into one sighting, local subnet first");

        // ---- healing ----
        var local = "DOR-WORKSTATION";
        AgentSighting Seen(string name, params string[] ips) => new(name, 7443, ips.Select(IPAddress.Parse).ToList(), "LAN discovery");

        var display = new MachineProfile { Name = "DOR-WORKSTATION (Primary Workstation)", Host = "192.168.1.126", Port = 7443 };
        var kind = EndpointHealing.Match(display, Seen("DOR-WORKSTATION", "192.168.1.140"), local);
        var plan = EndpointHealing.Plan(display, Seen("DOR-WORKSTATION", "192.168.1.140"), kind);
        check(kind == SightingMatch.DisplayName && plan.SetComputerName == "DOR-WORKSTATION" && plan.NewHost == "192.168.1.140",
            $"display-name profile without computer name: matched ({kind}), name learned, IP moved -> {plan.NewHost}");

        var byIp = new MachineProfile { Name = "laptop-test", Host = "192.168.1.126", Port = 7443, ComputerName = "LAPTOP-TEST" };
        plan = EndpointHealing.Plan(byIp, Seen("LAPTOP-TEST", "192.168.1.140"), EndpointHealing.Match(byIp, Seen("LAPTOP-TEST", "192.168.1.140"), local));
        check(plan is { OldHost: "192.168.1.126", NewHost: "192.168.1.140", LastKnownIp: "192.168.1.140", SetComputerName: null }, "IP -> IP: connects by IP, moved to the new IP");

        var multiNic = new MachineProfile { Name = "laptop-test", Host = "192.168.1.126", Port = 7443, ComputerName = "LAPTOP-TEST" };
        plan = EndpointHealing.Plan(multiNic, Seen("LAPTOP-TEST", "192.168.1.140", "192.168.1.126"), SightingMatch.ComputerName);
        check(plan.NewHost is null && plan.LastKnownIp == "192.168.1.126", "multi-NIC agent still answering on the saved IP: host unchanged (no flip-flop)");

        var byName = new MachineProfile { Name = "laptop-test", Host = "laptop-test.local", Port = 7443 };
        kind = EndpointHealing.Match(byName, Seen("LAPTOP-TEST", "192.168.1.140"), local);
        plan = EndpointHealing.Plan(byName, Seen("LAPTOP-TEST", "192.168.1.140"), kind);
        check(kind == SightingMatch.HostName && plan.NewHost is null && plan.LastKnownIp == "192.168.1.140" && plan.SetComputerName == "LAPTOP-TEST",
            "name host 'laptop-test.local': matched by name, host untouched, last known IP recorded");

        var reassigned = new MachineProfile { Name = "Old PC", Host = "192.168.1.140", Port = 7443, ComputerName = "OLD-PC" };
        check(EndpointHealing.Match(reassigned, Seen("LAPTOP-TEST", "192.168.1.140"), local) == SightingMatch.None,
            "a saved computer name is authoritative: another machine now on the same IP does not match");

        var legacyIp = new MachineProfile { Name = "Living room", Host = "192.168.1.42", Port = 7443 };
        check(EndpointHealing.Match(legacyIp, Seen("MEDIA-PC", "192.168.1.42"), local) == SightingMatch.HostIp, "legacy profile (no name) matched by its IP learns the computer name");
        check(EndpointHealing.Match(new MachineProfile { Name = "x", Host = "192.168.1.42", Port = 7000 }, Seen("MEDIA-PC", "192.168.1.42"), local) == SightingMatch.None,
            "fallback matches require the same port");

        var localhost = new MachineProfile { Name = "DOR-WORKSTATION (Local)", Host = "localhost", Port = 7443 };
        kind = EndpointHealing.Match(localhost, Seen("DOR-WORKSTATION", "192.168.1.20"), local);
        plan = EndpointHealing.Plan(localhost, Seen("DOR-WORKSTATION", "192.168.1.20"), kind);
        check(kind == SightingMatch.HostName && plan.NewHost is null, "localhost profile matches this PC's agent and keeps 'localhost'");
        check(EndpointHealing.Match(new MachineProfile { Name = "LAPTOP-TEST", Host = "192.168.1.9", Kind = EndpointKind.Nas }, Seen("LAPTOP-TEST", "192.168.1.9"), local) == SightingMatch.None,
            "appliances / agentless endpoints are never healed");

        // Prefer the stronger match: the display-name profile wins over a weaker IP match of another reply.
        var best = EndpointHealing.MatchAll([display], [Seen("STRANGER", "192.168.1.126"), Seen("DOR-WORKSTATION", "192.168.1.140")], local);
        check(best.Count == 1 && best[0].Sighting.MachineName == "DOR-WORKSTATION", "best match per profile (display name beats a stranger on the old IP)");

        // Announces: only endpoints with the same computer name AND the pinned certificate thumbprint.
        var thumb = new string('A', 64);
        var announce = new AgentSighting("LAPTOP-TEST", 7443, [IPAddress.Parse("10.20.0.7")], "signed agent announce", thumb);
        var pinnedOk = new MachineProfile { Name = "laptop", Host = "192.168.1.126", Port = 7443, ComputerName = "LAPTOP-TEST", AgentCertThumbprint = thumb };
        var pinnedOther = new MachineProfile { Name = "laptop", Host = "192.168.1.126", Port = 7443, ComputerName = "LAPTOP-TEST", AgentCertThumbprint = new string('B', 64) };
        var noName = new MachineProfile { Name = "LAPTOP-TEST", Host = "192.168.1.126", Port = 7443 };
        check(EndpointHealing.MatchAll([pinnedOk], [announce], local).Count == 1, "announce with the pinned thumbprint heals the endpoint");
        check(EndpointHealing.MatchAll([pinnedOther, noName], [announce], local).Count == 0,
            "announce with another thumbprint, or for an endpoint without a confirmed computer name, changes nothing");

        // Heal(): applies, logs the move and saves once.
        var fleet = new List<MachineProfile>
        {
            new() { Name = "laptop-test", Host = "192.168.1.126", Port = 7443, ComputerName = "LAPTOP-TEST" },
            new() { Name = "nas", Host = "nas.local", Port = 7443, ComputerName = "NAS-BOX" },
        };
        var saves = 0;
        var log = new List<string>();
        var report = EndpointLocator.Heal(fleet, [Seen("LAPTOP-TEST", "192.168.1.140"), Seen("NAS-BOX", "192.168.1.77"), Seen("NEW-PC", "192.168.1.88")],
            () => saves++, (c, m) => log.Add($"{c}: {m}"));
        check(fleet[0].Host == "192.168.1.140" && fleet[1].Host == "nas.local" && fleet[1].LastKnownIp == "192.168.1.77",
            "Heal: IP endpoint moved, name endpoint untouched (last IP recorded)");
        check(saves == 1 && log.Any(l => l.Contains("laptop-test moved 192.168.1.126:7443 → 192.168.1.140:7443")),
            $"Heal saves once and logs the move -> saves={saves}, log={string.Join(" | ", log)}");
        check(report.Unmatched.Count == 1 && report.Unmatched[0].MachineName == "NEW-PC", "unmatched agents are reported (Scan LAN adds them)");
        saves = 0;
        EndpointLocator.Heal(fleet, [Seen("LAPTOP-TEST", "192.168.1.140")], () => saves++, (_, _) => { });
        check(saves == 0, "nothing changed -> no save");

        // ---- persistence + card text ----
        var legacy = JsonSerializer.Deserialize<List<MachineProfile>>("[{\"Name\":\"laptop-test\",\"Host\":\"192.168.1.126\",\"Port\":7443}]")!;
        check(legacy[0].ComputerName == "" && legacy[0].LastKnownIp == "" && legacy[0].KnownAddresses.Count == 0 && legacy[0].AgentCertThumbprint == "",
            "machines.json from v2.2 (no new fields) loads with empty defaults");
        var json = JsonSerializer.Serialize(fleet);
        var back = JsonSerializer.Deserialize<List<MachineProfile>>(json)!;
        check(back[0].ComputerName == "LAPTOP-TEST" && back[0].LastKnownIp == "192.168.1.140" && back[0].KnownAddresses.SequenceEqual(["192.168.1.140"])
              && !json.Contains("DialOverride") && !json.Contains("AddressLine"),
            "new fields round-trip; session-only / display fields are not persisted");

        check(fleet[0].AddressLine == "192.168.1.140:7443 · LAPTOP-TEST" && fleet[0].ShowIpAutoHint, $"card (by IP) -> '{fleet[0].AddressLine}' + IP auto-updates hint");
        check(fleet[1].AddressLine == "nas.local · 192.168.1.77:7443" && !fleet[1].ShowIpAutoHint, $"card (by name) -> '{fleet[1].AddressLine}'");
        check(localhost.AddressLine == "localhost:7443", "card (localhost) shows only the address");
        fleet[1].DialOverride = "192.168.1.77";
        fleet[1].Host = "NAS-BOX";
        check(fleet[1].DialOverride is null && fleet[1].DialHost == "NAS-BOX", "changing Host clears the session dial override");
    }
}
