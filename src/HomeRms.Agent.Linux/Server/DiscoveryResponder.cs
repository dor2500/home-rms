using System.Net;
using System.Net.Sockets;
using System.Text;
using HomeRms.Shared.Network;
using HomeRms.Agent.Linux.Core;

namespace HomeRms.Agent.Linux.Server;

/// <summary>
/// Listens on UDP 7443 for Home RMS LAN discovery probes ("HOMERMS_DISCOVER_V1") and replies
/// with "HOMERMS_AGENT_V1|{MachineName}|{GrpcPort}|{ip1,ip2,...}" (addresses: v2.3+) (RFC1918 LAN only, rate-limited).
/// </summary>
public sealed class DiscoveryResponder(int grpcPort, ActivityLog log) : IDisposable
{
    public const int DiscoveryUdpPort = RmsNetwork.DiscoveryUdpPort;
    public const string ProbeToken = RmsNetwork.ProbeToken;
    public const string ReplyPrefix = RmsNetwork.ReplyPrefix;

    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public void Start()
    {
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryUdpPort));
            _ = Task.Run(() => ListenLoopAsync(_cts.Token));
            log.Write($"[discovery] UDP responder active on port {DiscoveryUdpPort}");
        }
        catch (Exception ex)
        {
            log.Write($"[discovery] UDP responder could not bind port {DiscoveryUdpPort}: {ex.Message}");
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        if (_udp is null) return;
        var lastReplyUtc = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await _udp.ReceiveAsync(ct);
                if (received.Buffer.Length > 64 || !IsPrivateLanIp(received.RemoteEndPoint.Address))
                    continue;

                var now = DateTime.UtcNow;
                if ((now - lastReplyUtc).TotalMilliseconds < 200)
                    continue;

                var msg = Encoding.UTF8.GetString(received.Buffer).Trim();
                if (string.Equals(msg, ProbeToken, StringComparison.Ordinal))
                {
                    lastReplyUtc = now;
                    var reply = RmsNetwork.BuildDiscoveryReply(Environment.MachineName, grpcPort, RmsNetwork.GetLocalAddresses());
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    await _udp.SendAsync(bytes, received.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private static bool IsPrivateLanIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp?.Close(); _udp?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
