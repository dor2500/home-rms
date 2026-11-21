using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace HomeRms.Controller.Services;

/// <summary>
/// Looks up the MAC address of an IPv4 neighbour (ARP, iphlpapi SendARP) so Wake-on-LAN keeps working
/// after an endpoint's IP changes. Only meaningful for addresses on a directly attached subnet.
/// </summary>
public static class ArpLookup
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLen);

    /// <summary>"AA-BB-CC-DD-EE-FF", or null when the address is not an on-link IPv4 neighbour or does not answer.</summary>
    public static string? TryGetMac(IPAddress ip, IReadOnlyList<LocalSubnet> localSubnets)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return null;
        if (!localSubnets.Any(s => s.Contains(ip))) return null;
        if (localSubnets.Any(s => s.Address.Equals(ip))) return null; // this PC: Wake-on-LAN to itself is pointless

        var mac = new byte[6];
        var len = (uint)mac.Length;
        var dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        var rc = SendARP(dest, 0, mac, ref len);
        if (rc != 0 || len != 6 || mac.All(b => b == 0)) return null;
        return string.Join("-", mac.Select(b => b.ToString("X2")));
    }
}
