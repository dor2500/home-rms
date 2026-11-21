using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HomeRms.Shared.Hosts;

/// <summary>A blocked hostname some local program tried to reach.</summary>
public readonly record struct SinkholeHit(string Domain, string Protocol, DateTimeOffset Time);

/// <summary>
/// Loopback-only "sinkhole" for blocked domains: while the hosts file maps blocked names to the listener's
/// loopback IP, browsers connect here. HTTP requests get a tiny 403 page; HTTPS connections are only
/// peeked (TLS ClientHello → SNI, nothing is decrypted or answered) and then reset. Each hit for a blocked
/// name is reported through <c>onHit</c>.
/// <para>Bounded: at most <see cref="MaxConcurrent"/> connections, <see cref="ConnectionTimeout"/> each,
/// <see cref="SinkholeProtocol.MaxPeekBytes"/> read per connection. Bind failures never throw: the result
/// says which ports are bound and why the others are not.</para>
/// </summary>
public sealed class SinkholeListener : IDisposable
{
    public const int MaxConcurrent = 64;
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(3);

    private static readonly byte[] Http403 = Encoding.ASCII.GetBytes(
        "HTTP/1.1 403 Forbidden\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 19\r\n" +
        "Cache-Control: no-store\r\nConnection: close\r\n\r\nBlocked by Home RMS");

    private readonly IPAddress _ip;
    private readonly int _httpPort;
    private readonly int _httpsPort;
    private readonly Func<string, string?> _match;
    private readonly Action<SinkholeHit> _onHit;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpListener> _listeners = new();
    private readonly List<Task> _loops = new();

    /// <param name="match">Returns the blocked domain a requested host belongs to, or null to ignore it.</param>
    public SinkholeListener(IPAddress ip, int httpPort, int httpsPort, Func<string, string?> match, Action<SinkholeHit> onHit, Action<string> log)
    {
        _ip = ip;
        _httpPort = httpPort;
        _httpsPort = httpsPort;
        _match = match;
        _onHit = onHit;
        _log = log;
    }

    public bool HttpBound { get; private set; }
    public bool HttpsBound { get; private set; }
    /// <summary>The loopback address the listener binds (dotted IPv4 text).</summary>
    public string Ip => _ip.ToString();
    public int HttpPort => _httpPort;
    public int HttpsPort => _httpsPort;
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Binds both ports (best effort) on IPv4 loopback and IPv6 ::1, and starts the accept loops.</summary>
    public void Start()
    {
        var warnings = new List<string>();
        HttpBound = TryBind(_ip, _httpPort, isTls: false, warnings);
        HttpsBound = TryBind(_ip, _httpsPort, isTls: true, warnings);
        // Also bind ::1 (best-effort, no warning if IPv6 is disabled) so browsers preferring IPv6 hit the sinkhole instead of failing with ERR_ADDRESS_INVALID.
        if (System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            var v6Warnings = new List<string>();
            TryBind(IPAddress.IPv6Loopback, _httpPort, isTls: false, v6Warnings);
            TryBind(IPAddress.IPv6Loopback, _httpsPort, isTls: true, v6Warnings);
        }
        Warnings = warnings;
        foreach (var w in warnings) _log("[blocker] WARNING " + w);
        if (HttpBound || HttpsBound)
            _log($"[blocker] listener running on {_ip} (HTTP {(HttpBound ? _httpPort : "-")}, HTTPS {(HttpsBound ? _httpsPort : "-")})");
    }

    private bool TryBind(IPAddress ip, int port, bool isTls, List<string> warnings)
    {
        TcpListener? l = null;
        try
        {
            l = new TcpListener(ip, port);
            l.Server.ExclusiveAddressUse = OperatingSystem.IsWindows();
            l.Start(128);
            _listeners.Add(l);
            _loops.Add(AcceptLoopAsync(l, isTls, _cts.Token));
            return true;
        }
        catch (SocketException ex)
        {
            l?.Stop();
            var why = ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => "is already in use by another program",
                SocketError.AccessDenied => "cannot be opened (access denied)",
                SocketError.AddressNotAvailable => "is not available on this machine",
                _ => $"could not be bound ({ex.SocketErrorCode})",
            };
            warnings.Add($"Port {port} ({(isTls ? "HTTPS" : "HTTP")}) on {ip} {why}.");
            return false;
        }
        catch (Exception ex)
        {
            l?.Stop();
            warnings.Add($"Port {port} ({(isTls ? "HTTPS" : "HTTP")}) on {ip} could not be bound: {ex.Message}");
            return false;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool isTls, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket sock;
            try { sock = await listener.AcceptSocketAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException) { continue; }

            if (!_slots.Wait(0))
            {
                Reset(sock); // over capacity: drop instead of queueing
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(sock, isTls, ct); }
                catch { /* a single bad connection must never take the listener down */ }
                finally
                {
                    _slots.Release();
                    try { sock.Dispose(); } catch { }
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(Socket sock, bool isTls, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectionTimeout);
        var buf = new byte[SinkholeProtocol.MaxPeekBytes];
        var n = 0;
        string? host = null;
        try
        {
            while (n < buf.Length)
            {
                var r = await sock.ReceiveAsync(buf.AsMemory(n), SocketFlags.None, timeout.Token);
                if (r <= 0) break;
                n += r;
                if (isTls)
                {
                    var st = SinkholeProtocol.TryParseSni(buf.AsSpan(0, n), out host);
                    if (st != SniParseStatus.NeedMore) break;
                }
                else
                {
                    host = SinkholeProtocol.TryParseHttpHost(buf.AsSpan(0, n), final: false, out var complete);
                    if (complete) break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }

        if (!isTls && host is null && n > 0)
            host = SinkholeProtocol.TryParseHttpHost(buf.AsSpan(0, n), final: true, out _);

        if (host is not null && _match(host) is { } domain)
        {
            try { _onHit(new SinkholeHit(domain, isTls ? "HTTPS" : "HTTP", DateTimeOffset.UtcNow)); }
            catch (Exception ex) { _log($"[blocker] event handler failed: {ex.Message}"); }
        }

        if (isTls)
        {
            Reset(sock);
            return;
        }
        try
        {
            using var send = new CancellationTokenSource(ConnectionTimeout);
            await sock.SendAsync(Http403, SocketFlags.None, send.Token);
            sock.Shutdown(SocketShutdown.Both);
        }
        catch { /* client went away */ }
    }

    private static void Reset(Socket sock)
    {
        try
        {
            sock.LingerState = new LingerOption(true, 0); // close with RST: no TLS alert, no half-open state
            sock.Close();
        }
        catch { }
    }

    /// <summary>Stops accepting and releases both ports immediately.</summary>
    public void Stop()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        foreach (var l in _listeners)
        {
            try { l.Stop(); } catch { }
        }
        try { Task.WaitAll(_loops.ToArray(), TimeSpan.FromSeconds(2)); } catch { }
        _listeners.Clear();
        _loops.Clear();
        HttpBound = HttpsBound = false;
        _log($"[blocker] listener on {_ip} stopped (ports released)");
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
