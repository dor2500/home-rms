using System.Net;
using System.Net.Sockets;
using System.Text;
using HomeRms.Agent.Core;

namespace HomeRms.Agent.Server;

/// <summary>
/// Listens on UDP for Home RMS LAN discovery probes ("HOMERMS_DISCOVER_V1") and replies
/// with "HOMERMS_AGENT_V1|{MachineName}|{GrpcPort}" so the Controller can discover all
/// LAN devices that have the Home RMS service installed and then verify them over mTLS.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    public const int DiscoveryUdpPort = 7443;
    public const string ProbeToken = "HOMERMS_DISCOVER_V1";
    public const string ReplyPrefix = "HOMERMS_AGENT_V1";

    private readonly int _grpcPort;
    private readonly ActivityLog _log;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public DiscoveryResponder(int grpcPort, ActivityLog log)
    {
        _grpcPort = grpcPort;
        _log = log;
    }

    public void Start()
    {
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryUdpPort));
            _ = Task.Run(() => ListenLoopAsync(_cts.Token));
            _log.Write($"[discovery] UDP responder active on port {DiscoveryUdpPort}");
        }
        catch (Exception ex)
        {
            _log.Write($"[discovery] UDP responder could not bind port {DiscoveryUdpPort}: {ex.Message}");
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        if (_udp is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await _udp.ReceiveAsync(ct);
                var msg = Encoding.UTF8.GetString(received.Buffer).Trim();
                if (string.Equals(msg, ProbeToken, StringComparison.Ordinal))
                {
                    var reply = $"{ReplyPrefix}|{Environment.MachineName}|{_grpcPort}";
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    await _udp.SendAsync(bytes, received.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* ignore malformed UDP packets */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp?.Close(); _udp?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
