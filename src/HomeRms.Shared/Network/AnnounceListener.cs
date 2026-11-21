using System.Net;
using System.Net.Sockets;

namespace HomeRms.Shared.Network;

/// <summary>
/// Controller-side UDP listener for signed agent announces. Only announces that pass
/// <see cref="AnnounceVerifier"/> are raised; everything else is reported to <c>debugLog</c>
/// (rate-limited) and dropped.
/// </summary>
public sealed class AnnounceListener(int port, AnnounceVerifier verifier, Action<string>? debugLog = null) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private DateTime _lastDebugUtc = DateTime.MinValue;

    public int Port { get; private set; } = port;

    /// <summary>Raised on a thread-pool thread for every verified announce, with the packet's source address.</summary>
    public event Action<AnnounceResult, IPEndPoint>? Verified;

    /// <summary>Binds the port (IPv4 + IPv6 dual mode). Returns false with the reason when the port is unavailable.</summary>
    public bool Start(out string? error)
    {
        error = null;
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetworkV6);
            udp.Client.DualMode = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, Port));
            Port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            _udp = udp;
        }
        catch (SocketException ex)
        {
            error = ex.Message;
            return false;
        }
        _ = Task.Run(() => LoopAsync(_cts.Token));
        return true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp is not null)
        {
            UdpReceiveResult pkt;
            try { pkt = await _udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; } // ICMP echo of an earlier send
            catch (SocketException ex) { Debug($"announce receive failed: {ex.Message}"); continue; }

            AnnounceResult result;
            try { result = verifier.Verify(pkt.Buffer); }
            catch (Exception ex) { result = AnnounceResult.Fail("verifier error: " + ex.Message); }

            var from = new IPEndPoint(RmsNetwork.Normalize(pkt.RemoteEndPoint.Address), pkt.RemoteEndPoint.Port);
            if (!result.Ok)
            {
                Debug($"ignored announce from {from.Address}: {result.Reason}");
                continue;
            }
            try { Verified?.Invoke(result, from); }
            catch (Exception ex) { Debug($"announce handler failed: {ex.Message}"); }
        }
    }

    private void Debug(string message)
    {
        if (debugLog is null) return;
        var now = DateTime.UtcNow;
        if (now - _lastDebugUtc < TimeSpan.FromSeconds(5)) return; // a flood of junk must not flood the log
        _lastDebugUtc = now;
        debugLog(message);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp?.Dispose(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
