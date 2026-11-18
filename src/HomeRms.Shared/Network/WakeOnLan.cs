using System.Net;
using System.Net.Sockets;

namespace HomeRms.Shared.Network;

public static class WakeOnLan
{
    public static byte[] BuildMagicPacket(string macAddress)
    {
        var hex = new string(macAddress.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12)
            throw new ArgumentException("MAC address must contain 12 hex digits.", nameof(macAddress));

        var mac = Convert.FromHexString(hex);
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
            Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    public static async Task SendAsync(string macAddress, int port = 9)
    {
        var packet = BuildMagicPacket(macAddress);
        using var udp = new UdpClient { EnableBroadcast = true };
        await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, port));
    }
}
