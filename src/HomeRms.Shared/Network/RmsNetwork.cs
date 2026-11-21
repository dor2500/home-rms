using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HomeRms.Shared.Network;

/// <summary>A parsed LAN discovery reply ("HOMERMS_AGENT_V1|name|port[|ip1,ip2,...]").</summary>
public sealed record DiscoveryReply(string MachineName, int Port, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Wire constants and helpers shared by the agents (Windows / Linux) and the controllers for
/// LAN discovery and the signed "call home" announce.
/// </summary>
public static class RmsNetwork
{
    /// <summary>UDP port the agents listen on for discovery probes (same number as the gRPC port).</summary>
    public const int DiscoveryUdpPort = 7443;

    /// <summary>UDP port the controller listens on for signed agent announces.</summary>
    public const int AnnounceUdpPort = 7445;

    public const string ProbeToken = "HOMERMS_DISCOVER_V1";
    public const string ReplyPrefix = "HOMERMS_AGENT_V1";

    /// <summary>Most addresses carried in a discovery reply or an announce.</summary>
    public const int MaxAddresses = 8;

    /// <summary>
    /// Builds the discovery reply. The 4th field (comma-separated addresses) is an extension:
    /// controllers before v2.3 only read the first three fields and ignore the rest.
    /// </summary>
    public static string BuildDiscoveryReply(string machineName, int port, IEnumerable<IPAddress> addresses)
    {
        var list = string.Join(",", addresses.Take(MaxAddresses).Select(a => a.ToString()));
        return list.Length == 0
            ? $"{ReplyPrefix}|{machineName}|{port}"
            : $"{ReplyPrefix}|{machineName}|{port}|{list}";
    }

    /// <summary>Parses old 3-field and new 4-field replies; null for anything that is not a valid reply.</summary>
    public static DiscoveryReply? ParseDiscoveryReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('|');
        if (parts.Length < 3 || parts[0] != ReplyPrefix) return null;

        var name = parts[1].Trim();
        if (!IsPlausibleMachineName(name)) return null;
        // Older controllers fell back to the default port on a bad value; keep that behaviour.
        if (!int.TryParse(parts[2], out var port) || port is < 1 or > 65535) port = RmsPaths.DefaultPort;

        var addresses = parts.Length >= 4 ? ParseAddressList(parts[3]) : [];
        return new DiscoveryReply(name, port, addresses);
    }

    /// <summary>Parses "ip1,ip2,..." keeping valid, distinct, non-loopback addresses (at most <see cref="MaxAddresses"/>).</summary>
    public static IReadOnlyList<IPAddress> ParseAddressList(string? list)
    {
        var result = new List<IPAddress>();
        if (string.IsNullOrWhiteSpace(list)) return result;
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (result.Count >= MaxAddresses) break;
            if (!IPAddress.TryParse(raw, out var ip)) continue;
            ip = Normalize(ip);
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) continue;
            if (!result.Contains(ip)) result.Add(ip);
        }
        return result;
    }

    /// <summary>Machine names never contain separators or control characters; cap the length as DNS does.</summary>
    public static bool IsPlausibleMachineName(string name) =>
        name.Length is > 0 and <= 253 && !name.Any(c => char.IsControl(c) || c is '|' or ',' or ' ');

    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>
    /// Every usable address of this machine: up, non-loopback IPv4 (APIPA excluded) first, then global /
    /// unique-local IPv6 (no link-local). Interfaces with a default gateway come first.
    /// </summary>
    public static IReadOnlyList<IPAddress> GetLocalAddresses(bool includeIPv6 = true)
    {
        var withGateway = new List<IPAddress>();
        var others = new List<IPAddress>();
        var v6 = new List<IPAddress>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return []; }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            var hasGateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));

            foreach (var ua in props.UnicastAddresses)
            {
                var ip = ua.Address;
                if (IPAddress.IsLoopback(ip)) continue;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    var b = ip.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // APIPA: not routable, useless to a controller
                    (hasGateway ? withGateway : others).Add(ip);
                }
                else if (includeIPv6 && ip.AddressFamily == AddressFamily.InterNetworkV6 &&
                         !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast && !ip.IsIPv6SiteLocal && !ip.IsIPv4MappedToIPv6 &&
                         !IsTemporaryIPv6(ua))
                {
                    v6.Add(new IPAddress(ip.GetAddressBytes())); // drop the scope id
                }
            }
        }
        // At most two IPv6 addresses: they are a fallback, and the packets must stay small.
        return withGateway.Concat(others).Concat(v6.Distinct().Take(2)).Distinct().ToList();
    }

    /// <summary>RFC 4941 privacy addresses rotate daily; announcing them is useless (Windows reports them, Linux may not).</summary>
    private static bool IsTemporaryIPv6(UnicastIPAddressInformation ua)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return ua.SuffixOrigin == SuffixOrigin.Random; }
        catch (NetworkInformationException) { return false; }
    }
}
