using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Controller.Themes;
using HomeRms.Shared.Network;

namespace HomeRms.Controller.Services;

/// <summary>Pure helpers for endpoint addresses (computer names vs IP addresses). No I/O.</summary>
public static class EndpointAddress
{
    /// <summary>A literal IPv4 dotted quad or IPv6 address ("123" is not an IP here, unlike IPAddress.TryParse).</summary>
    public static bool IsIpAddress(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim();
        if (host.Contains(':'))
            return IPAddress.TryParse(host.Trim('[', ']'), out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p) <= 255);
    }

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().TrimEnd('.');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IsIpAddress(host) && IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Validates an endpoint host: an IPv4 / IPv6 address, or a computer name / DNS name made of RFC 1123
    /// labels (letters, digits, hyphens; 1-63 characters; no leading or trailing hyphen), e.g.
    /// <c>LAPTOP-TEST</c>, <c>laptop-test.local</c> or <c>nas.home.example</c>.
    /// </summary>
    public static bool IsValidHost(string? host, out string error)
    {
        error = "";
        host = host?.Trim() ?? "";
        if (host.Length == 0) { error = "Enter a computer name or IP address."; return false; }
        if (IsIpAddress(host)) return true;
        if (host.Contains(':')) { error = "That is not a valid IPv6 address."; return false; }

        var name = host.EndsWith('.') ? host[..^1] : host;
        if (name.Length is 0 or > 253) { error = "Computer names are at most 253 characters."; return false; }
        var labels = name.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63) { error = "Each part of a name must be 1-63 characters (no empty parts)."; return false; }
            if (!label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            { error = "Computer names may contain only letters, digits and hyphens (and dots between parts)."; return false; }
            if (label[0] == '-' || label[^1] == '-') { error = "A name part cannot start or end with a hyphen."; return false; }
        }
        if (labels.All(l => l.All(char.IsAsciiDigit))) { error = "That looks like an incomplete IP address (use four numbers, e.g. 192.168.1.20)."; return false; }
        return true;
    }

    /// <summary>"laptop-test.local" / "LAPTOP-TEST.home.lan" -> "LAPTOP-TEST"; IP addresses are returned unchanged.</summary>
    public static string ShortName(string? name)
    {
        var n = (name ?? "").Trim().TrimEnd('.');
        if (IsIpAddress(n)) return n;
        var dot = n.IndexOf('.');
        return dot > 0 ? n[..dot] : n;
    }

    /// <summary>Computer-name equality ignoring case and any ".local" / DNS domain suffix.</summary>
    public static bool NamesEqual(string? a, string? b)
    {
        var x = ShortName(a);
        var y = ShortName(b);
        return x.Length > 0 && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a display name starts with the computer name as a whole word:
    /// "DOR-WORKSTATION (Primary Workstation)" starts with "DOR-WORKSTATION"; "PC2 desk" does not start with "PC".
    /// </summary>
    public static bool DisplayNameStartsWith(string? displayName, string? machineName)
    {
        var d = (displayName ?? "").Trim();
        var m = ShortName(machineName);
        if (m.Length == 0 || !d.StartsWith(m, StringComparison.OrdinalIgnoreCase)) return false;
        if (d.Length == m.Length) return true;
        var next = d[m.Length];
        return !char.IsLetterOrDigit(next) && next is not '-' and not '_';
    }

    /// <summary>IPv4 on a local subnet first, then other IPv4, then IPv6 (stable within each group).</summary>
    public static List<IPAddress> Rank(IEnumerable<IPAddress> candidates, IReadOnlyList<LocalSubnet> localSubnets) =>
        candidates.Select(RmsNetwork.Normalize).Distinct()
            .Select((ip, i) => (ip, i))
            .OrderBy(t => t.ip.AddressFamily != AddressFamily.InterNetwork ? 2 : localSubnets.Any(s => s.Contains(t.ip)) ? 0 : 1)
            .ThenBy(t => t.i)
            .Select(t => t.ip)
            .ToList();
}

/// <summary>One IPv4 network this controller is attached to.</summary>
public sealed record LocalSubnet(IPAddress Address, IPAddress Mask)
{
    public bool Contains(IPAddress ip)
    {
        ip = RmsNetwork.Normalize(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = Address.GetAddressBytes();
        var m = Mask.GetAddressBytes();
        var b = ip.GetAddressBytes();
        for (var i = 0; i < 4; i++)
            if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        return true;
    }

    /// <summary>Directed broadcast (e.g. 192.168.1.255 for 192.168.1.20/24); null for /31 and /32.</summary>
    public IPAddress? Broadcast
    {
        get
        {
            var a = Address.GetAddressBytes();
            var m = Mask.GetAddressBytes();
            if (m[3] is 0xFF or 0xFE && m[0] == 0xFF && m[1] == 0xFF && m[2] == 0xFF) return null;
            var b = new byte[4];
            for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            return new IPAddress(b);
        }
    }
}

/// <summary>
/// One agent seen on the network (a discovery reply or a verified announce), with every address it can be
/// reached at: the packet source first, then the addresses the agent reported for itself.
/// </summary>
public sealed record AgentSighting(string MachineName, int Port, IReadOnlyList<IPAddress> Addresses, string Via, string? CertThumbprint = null);

public enum SightingMatch
{
    None = 0,
    /// <summary>Profile has no computer name yet and its IP host is one of the agent's addresses.</summary>
    HostIp = 1,
    /// <summary>Profile has no computer name yet; its display name starts with the agent's name.</summary>
    DisplayName = 2,
    /// <summary>Profile connects by a name equal to the agent's (or is localhost and the agent is this PC).</summary>
    HostName = 3,
    /// <summary>Profile's saved computer name equals the agent's.</summary>
    ComputerName = 4,
}

/// <summary>What healing changes on one profile (null fields stay unchanged).</summary>
public sealed record HealPlan(string? SetComputerName, string? OldHost, string? NewHost, int? NewPort, string? LastKnownIp, List<string> KnownAddresses)
{
    public bool MovesHost => NewHost is not null || NewPort is not null;
}

/// <summary>Pure matching / healing rules (unit-tested in ControllerChecks).</summary>
public static class EndpointHealing
{
    public static bool IsAgentEndpoint(MachineProfile p) =>
        p.Kind == EndpointKind.WindowsAgent || (p.Kind == EndpointKind.Linux && p.ConnectionMode == ConnectionMode.Agent);

    public static SightingMatch Match(MachineProfile p, AgentSighting s, string localMachineName)
    {
        if (!IsAgentEndpoint(p)) return SightingMatch.None;

        // A saved computer name is authoritative: an IP that now belongs to another machine never matches.
        if (!string.IsNullOrWhiteSpace(p.ComputerName))
            return EndpointAddress.NamesEqual(p.ComputerName, s.MachineName) ? SightingMatch.ComputerName : SightingMatch.None;

        // Signed announces may only update endpoints whose computer name and certificate are already known.
        if (s.CertThumbprint is not null) return SightingMatch.None;
        if (p.Port != s.Port) return SightingMatch.None;

        var host = p.Host.Trim();
        if (EndpointAddress.IsLoopbackHost(host))
            return EndpointAddress.NamesEqual(s.MachineName, localMachineName) ? SightingMatch.HostName : SightingMatch.None;
        if (!EndpointAddress.IsIpAddress(host) && EndpointAddress.NamesEqual(host, s.MachineName)) return SightingMatch.HostName;
        if (EndpointAddress.DisplayNameStartsWith(p.Name, s.MachineName)) return SightingMatch.DisplayName;
        if (EndpointAddress.IsIpAddress(host) && s.Addresses.Any(a => a.ToString().Equals(host, StringComparison.OrdinalIgnoreCase)))
            return SightingMatch.HostIp;
        return SightingMatch.None;
    }

    /// <summary>For each profile the best-matching sighting (announces additionally need the pinned certificate thumbprint).</summary>
    public static List<(MachineProfile Profile, AgentSighting Sighting, SightingMatch Kind)> MatchAll(
        IEnumerable<MachineProfile> profiles, IReadOnlyList<AgentSighting> sightings, string localMachineName)
    {
        var result = new List<(MachineProfile, AgentSighting, SightingMatch)>();
        foreach (var p in profiles)
        {
            AgentSighting? best = null;
            var bestKind = SightingMatch.None;
            foreach (var s in sightings)
            {
                var kind = Match(p, s, localMachineName);
                if (kind == SightingMatch.None) continue;
                if (s.CertThumbprint is not null &&
                    (kind != SightingMatch.ComputerName || !string.Equals(p.AgentCertThumbprint, s.CertThumbprint, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (kind > bestKind) { best = s; bestKind = kind; }
            }
            if (best is not null) result.Add((p, best, bestKind));
        }
        return result;
    }

    /// <summary>
    /// The changes for one matched profile. An endpoint that connects by IP follows the agent to a new IP
    /// (staying on its current IP while that is still one of the agent's addresses, so multi-NIC agents do
    /// not flip-flop); an endpoint that connects by name or localhost keeps its host.
    /// </summary>
    public static HealPlan Plan(MachineProfile p, AgentSighting s, SightingMatch kind)
    {
        var addrs = s.Addresses.Select(a => RmsNetwork.Normalize(a).ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var host = p.Host.Trim();
        var hostIsIp = EndpointAddress.IsIpAddress(host) && !EndpointAddress.IsLoopbackHost(host);

        string? chosen = null;
        if (hostIsIp && addrs.Contains(host, StringComparer.OrdinalIgnoreCase)) chosen = host;
        else if (addrs.Contains(p.LastKnownIp, StringComparer.OrdinalIgnoreCase)) chosen = p.LastKnownIp;
        else if (addrs.Count > 0) chosen = addrs[0];

        var setName = string.IsNullOrWhiteSpace(p.ComputerName) ? s.MachineName : null;
        var newHost = hostIsIp && chosen is not null && !string.Equals(chosen, host, StringComparison.OrdinalIgnoreCase) ? chosen : null;
        var newPort = kind == SightingMatch.ComputerName && s.Port != p.Port ? s.Port : (int?)null;
        var lastKnown = chosen is not null && !string.Equals(chosen, p.LastKnownIp, StringComparison.OrdinalIgnoreCase) ? chosen : null;
        return new HealPlan(setName, newHost is null ? null : host, newHost, newPort, lastKnown, addrs);
    }

    /// <summary>Applies a plan; returns true when anything persisted changed.</summary>
    public static bool Apply(MachineProfile p, HealPlan plan)
    {
        var changed = false;
        if (plan.SetComputerName is not null) { p.ComputerName = plan.SetComputerName; changed = true; }
        if (plan.NewHost is not null) { p.Host = plan.NewHost; changed = true; }
        if (plan.NewPort is { } port) { p.Port = port; changed = true; }
        if (plan.LastKnownIp is not null) { p.LastKnownIp = plan.LastKnownIp; changed = true; }
        if (plan.KnownAddresses.Count > 0 && !plan.KnownAddresses.SequenceEqual(p.KnownAddresses, StringComparer.OrdinalIgnoreCase))
        {
            p.KnownAddresses = plan.KnownAddresses;
            changed = true;
        }
        return changed;
    }

    /// <summary>Parses one discovery packet; null when it is not a Home RMS agent reply.</summary>
    public static AgentSighting? ParseReplyPacket(byte[] buffer, IPAddress source)
    {
        if (buffer.Length is 0 or > 1024) return null;
        var reply = RmsNetwork.ParseDiscoveryReply(Encoding.UTF8.GetString(buffer));
        if (reply is null) return null;
        var src = RmsNetwork.Normalize(source);
        return new AgentSighting(reply.MachineName, reply.Port, [src, .. reply.Addresses.Where(a => !a.Equals(src))], "LAN discovery");
    }

    /// <summary>Merges replies of the same agent (several NICs, several probes) into one sighting each.</summary>
    public static List<AgentSighting> Merge(IEnumerable<AgentSighting> sightings, IReadOnlyList<LocalSubnet> localSubnets)
    {
        return sightings
            .GroupBy(s => (Name: EndpointAddress.ShortName(s.MachineName).ToUpperInvariant(), s.Port))
            .Select(g =>
            {
                var first = g.First();
                // Sources (where replies really came from) before self-reported addresses.
                var sources = g.Select(s => s.Addresses[0]);
                var reported = g.SelectMany(s => s.Addresses.Skip(1));
                var ranked = EndpointAddress.Rank(sources, localSubnets)
                    .Concat(EndpointAddress.Rank(reported, localSubnets)).Distinct().ToList();
                return first with { Addresses = ranked };
            })
            .ToList();
    }
}

public sealed record EndpointResolution(string Address, int Port, string Via);

public sealed record HealChange(MachineProfile Profile, HealPlan Plan, string OldDialHost, int OldPort);

public sealed record HealReport(IReadOnlyList<AgentSighting> Sightings, List<HealChange> Changes, List<AgentSighting> Unmatched);

/// <summary>
/// Finds agents on the network and follows endpoints whose IP address changed: LAN discovery (global
/// and per-NIC directed broadcasts), name resolution (DNS / LLMNR / NetBIOS / mDNS via the OS resolver)
/// and the other addresses an agent reported for itself.
/// </summary>
public static class EndpointLocator
{
    private static readonly HashSet<string> LoggedErrors = new(StringComparer.Ordinal);

    public static IReadOnlyList<LocalSubnet> GetLocalSubnets()
    {
        var list = new List<LocalSubnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                    if (ua.IPv4Mask is null || ua.IPv4Mask.Equals(IPAddress.Any)) continue;
                    list.Add(new LocalSubnet(ua.Address, ua.IPv4Mask));
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            LogOnce("Could not enumerate network adapters: " + ex.Message);
        }
        return list;
    }

    /// <summary>
    /// Broadcasts the discovery probe to 255.255.255.255 and to every adapter's directed subnet broadcast,
    /// three times during <paramref name="window"/>, and returns one merged sighting per agent.
    /// The window closing is the normal end; other socket errors are logged.
    /// </summary>
    public static async Task<IReadOnlyList<AgentSighting>> DiscoverAsync(TimeSpan window, CancellationToken ct)
    {
        var subnets = GetLocalSubnets();
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, RmsNetwork.DiscoveryUdpPort) };
        targets.AddRange(subnets.Select(s => s.Broadcast).OfType<IPAddress>().Distinct()
            .Select(b => new IPEndPoint(b, RmsNetwork.DiscoveryUdpPort)));

        var raw = new List<AgentSighting>();
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            // Windows reports an ICMP "port unreachable" of an earlier datagram as a receive error; ignore those.
            const int SioUdpConnReset = -1744830452;
            udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException ex)
        {
            LogOnce("LAN discovery could not open a UDP socket: " + ex.Message);
            return [];
        }

        using (udp)
        using (var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            windowCts.CancelAfter(window);
            var probe = Encoding.ASCII.GetBytes(RmsNetwork.ProbeToken);
            var sender = SendProbesAsync(udp, probe, targets, window, windowCts.Token);
            while (!windowCts.IsCancellationRequested)
            {
                try
                {
                    var pkt = await udp.ReceiveAsync(windowCts.Token);
                    if (EndpointHealing.ParseReplyPacket(pkt.Buffer, pkt.RemoteEndPoint.Address) is { } s) raw.Add(s);
                }
                catch (OperationCanceledException) { break; } // listen window closed (or caller cancelled)
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { }
                catch (SocketException ex)
                {
                    LogOnce("LAN discovery receive failed: " + ex.Message);
                    break;
                }
            }
            await sender;
        }
        ct.ThrowIfCancellationRequested();
        return EndpointHealing.Merge(raw, subnets);
    }

    private static async Task SendProbesAsync(UdpClient udp, byte[] probe, List<IPEndPoint> targets, TimeSpan window, CancellationToken ct)
    {
        var gap = TimeSpan.FromMilliseconds(Math.Max(100, window.TotalMilliseconds / 3));
        for (var round = 0; round < 3 && !ct.IsCancellationRequested; round++)
        {
            foreach (var t in targets)
            {
                try { await udp.SendAsync(probe, t, ct); }
                catch (OperationCanceledException) { return; }
                catch (SocketException ex)
                {
                    // e.g. a disconnected adapter or a network that refuses broadcasts; the other targets still go out.
                    LogOnce($"LAN discovery probe to {t.Address} failed: {ex.Message}");
                }
            }
            try { await Task.Delay(gap, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>TCP connect test with a short timeout (no TLS, nothing is sent).</summary>
    public static async Task<bool> TcpReachableAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (SocketException) { return false; }
    }

    /// <summary>Probes all candidates in parallel and returns the best-ranked reachable one.</summary>
    public static async Task<IPAddress?> FirstReachableAsync(IReadOnlyList<IPAddress> ranked, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (ranked.Count == 0) return null;
        var results = await Task.WhenAll(ranked.Select(ip => TcpReachableAsync(ip, port, timeout, ct)));
        for (var i = 0; i < ranked.Count; i++)
            if (results[i]) return ranked[i];
        return null;
    }

    /// <summary>OS name resolution (DNS, LLMNR, NetBIOS, mDNS) with a timeout; empty when the name does not resolve.</summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveNameAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(name, cts.Token);
            return EndpointAddress.Rank(addrs.Where(a => !IPAddress.IsLoopback(a) || EndpointAddress.IsLoopbackHost(name)), GetLocalSubnets());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; } // resolver too slow
        catch (SocketException) { return []; } // name not found: an expected outcome, not an error
    }

    /// <summary>
    /// Where <paramref name="p"/> can be reached now. By name: the resolver's answer (preferring IPv4 on a
    /// local subnet) if it accepts TCP; by IP: the host itself if it accepts TCP. Then the agent's other known
    /// addresses, then LAN discovery matched by computer name. Null when nothing answers.
    /// </summary>
    public static async Task<EndpointResolution?> ResolveAsync(MachineProfile p, CancellationToken ct)
    {
        var host = p.Host.Trim();
        var port = p.Port;
        if (host.Length == 0 || EndpointAddress.IsLoopbackHost(host)) return null;
        var subnets = GetLocalSubnets();
        var probeTimeout = TimeSpan.FromMilliseconds(900);

        if (EndpointAddress.IsIpAddress(host))
        {
            if (await TcpReachableAsync(IPAddress.Parse(host.Trim('[', ']')), port, probeTimeout, ct))
                return new EndpointResolution(host, port, "saved address");
        }
        else
        {
            var resolved = await ResolveNameAsync(host, TimeSpan.FromSeconds(3), ct);
            if (await FirstReachableAsync(resolved.Take(4).ToList(), port, probeTimeout, ct) is { } ip)
                return new EndpointResolution(ip.ToString(), port, "name resolution");
        }

        // Other addresses this agent reported (second NIC, VPN, another VLAN), same subnet first.
        var others = p.KnownAddresses.Append(p.LastKnownIp)
            .Where(a => EndpointAddress.IsIpAddress(a) && !string.Equals(a, host, StringComparison.OrdinalIgnoreCase))
            .Select(a => IPAddress.Parse(a.Trim('[', ']')));
        if (await FirstReachableAsync(EndpointAddress.Rank(others, subnets).Take(8).ToList(), port, probeTimeout, ct) is { } alt)
            return new EndpointResolution(alt.ToString(), port, "another known address");

        var name = !string.IsNullOrWhiteSpace(p.ComputerName) ? p.ComputerName : EndpointAddress.IsIpAddress(host) ? null : host;
        if (name is null) return null;
        var sightings = await DiscoverAsync(TimeSpan.FromMilliseconds(1500), ct);
        var match = sightings.FirstOrDefault(s => EndpointAddress.NamesEqual(s.MachineName, name));
        if (match is null || match.Addresses.Count == 0) return null;
        var pick = match.Addresses.FirstOrDefault(a => a.ToString() == p.LastKnownIp) ?? match.Addresses[0];
        return new EndpointResolution(pick.ToString(), match.Port, "LAN discovery");
    }

    /// <summary>
    /// Matches sightings (discovery replies or verified announces) to the saved endpoints and heals them:
    /// fills a missing computer name, moves endpoints that connect by IP to the agent's current IP, records
    /// the last known IP and the agent's addresses. Logs every move and saves once. Call on the UI thread.
    /// </summary>
    public static HealReport Heal(IList<MachineProfile> machines, IReadOnlyList<AgentSighting> sightings, Action save, Action<string, string> log)
    {
        var changes = new List<HealChange>();
        var used = new HashSet<AgentSighting>();
        var persisted = false;
        foreach (var (profile, sighting, kind) in EndpointHealing.MatchAll(machines.ToList(), sightings, Environment.MachineName))
        {
            used.Add(sighting);
            var plan = EndpointHealing.Plan(profile, sighting, kind);
            var oldDial = profile.DialHost;
            var oldPort = profile.Port;
            if (!EndpointHealing.Apply(profile, plan)) continue;
            persisted = true;
            changes.Add(new HealChange(profile, plan, oldDial, oldPort));
            if (plan.MovesHost)
                log("FLEET", $"{profile.DisplayTitle} moved {plan.OldHost ?? oldDial}:{oldPort} → {profile.Host}:{profile.Port} (found by {sighting.Via}).");
            else if (plan.SetComputerName is not null)
                log("FLEET", $"{profile.DisplayTitle} is computer {plan.SetComputerName}; its IP will be followed automatically.");
        }
        if (persisted) save();
        return new HealReport(sightings, changes, sightings.Where(s => !used.Contains(s)).ToList());
    }

    /// <summary>Discovery (unless sightings are given) followed by <see cref="Heal"/>.</summary>
    public static async Task<HealReport> HealAsync(IList<MachineProfile> machines, Action save, Action<string, string> log,
        CancellationToken ct, IReadOnlyList<AgentSighting>? sightings = null)
    {
        sightings ??= await DiscoverAsync(TimeSpan.FromMilliseconds(1500), ct);
        return Heal(machines, sightings, save, log);
    }

    /// <summary>True when the computer name resolves (OS resolver) to <paramref name="ip"/>.</summary>
    public static async Task<bool> NameResolvesToAsync(string name, IPAddress ip, CancellationToken ct)
    {
        var addrs = await ResolveNameAsync(name, TimeSpan.FromSeconds(2), ct);
        return addrs.Any(a => a.Equals(RmsNetwork.Normalize(ip)));
    }

    /// <summary>Connection-level failures worth a re-resolve (not auth, pairing or application errors).</summary>
    public static bool IsTransportError(Exception ex) => ex switch
    {
        RpcException rpc => rpc.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded,
        HttpRequestException or SocketException or TimeoutException => true,
        AggregateException agg => agg.InnerExceptions.Any(IsTransportError),
        _ => ex.InnerException is not null && IsTransportError(ex.InnerException),
    };

    private static void LogOnce(string message)
    {
        lock (LoggedErrors)
            if (!LoggedErrors.Add(message)) return;
        LogBus.Publish(LogBus.Activity, "LAN SCAN", message, PaletteTone.Warning);
    }
}
