using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HomeRms.Shared.Network;
using HomeRms.Shared.Security;

namespace HomeRms.Shared.Server;

public sealed class AgentAnnouncerOptions
{
    /// <summary>Controller UDP port announces are sent to.</summary>
    public int TargetPort { get; init; } = RmsNetwork.AnnounceUdpPort;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>JSON file remembering controller addresses (default: next to the pinned-controller store).</summary>
    public string? StorePath { get; init; }
    public int MaxControllers { get; init; } = 5;
}

/// <summary>
/// Agent "call home": remembers the source addresses of pinned controllers that connected over mTLS
/// (persisted, last few kept) and sends each of them a signed unicast announce with this machine's
/// current addresses on startup, when the network addresses change and every minute. This lets a
/// controller follow the agent across subnets / VPNs where broadcast discovery cannot reach.
/// </summary>
public sealed class AgentAnnouncer : IDisposable
{
    private sealed class KnownController
    {
        public string Address { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
    }

    private readonly X509Certificate2 _cert;
    private readonly int _grpcPort;
    private readonly AgentAnnouncerOptions _options;
    private readonly Action<string> _log;
    private readonly string _storePath;
    private readonly object _lock = new();
    private readonly List<KnownController> _controllers = [];
    private readonly ConcurrentDictionary<string, DateTime> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public AgentAnnouncer(X509Certificate2 agentCertWithKey, int grpcPort, AgentAnnouncerOptions? options, string? trustedStorePath, Action<string> log)
    {
        _cert = agentCertWithKey;
        _grpcPort = grpcPort;
        _options = options ?? new AgentAnnouncerOptions();
        _log = log;
        var dir = Path.GetDirectoryName(trustedStorePath ?? TrustedControllerStore.DefaultStorePath) ?? ".";
        _storePath = _options.StorePath ?? Path.Combine(dir, "known-controllers.json");
    }

    /// <summary>Snapshot of the remembered controller addresses (most recent first).</summary>
    public IReadOnlyList<string> KnownControllers
    {
        get { lock (_lock) return _controllers.Select(c => c.Address).ToList(); }
    }

    public void Start()
    {
        Load();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>
    /// Called for each incoming mTLS request: remembers <paramref name="remote"/> when the client
    /// certificate is a pinned controller. Cheap: each address is checked at most every 10 minutes.
    /// </summary>
    public void ObserveController(IPAddress? remote, X509Certificate2? clientCert, Func<X509Certificate2, bool> isPinned)
    {
        if (remote is null || clientCert is null) return;
        var ip = RmsNetwork.Normalize(remote).ToString();
        var now = DateTime.UtcNow;
        if (_observed.TryGetValue(ip, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
        _observed[ip] = now;
        if (!isPinned(clientCert)) return;

        bool isNew;
        lock (_lock)
        {
            var existing = _controllers.FirstOrDefault(c => c.Address == ip);
            isNew = existing is null;
            if (existing is not null) _controllers.Remove(existing);
            _controllers.Insert(0, new KnownController { Address = ip, LastSeenUtc = now });
            while (_controllers.Count > Math.Max(1, _options.MaxControllers)) _controllers.RemoveAt(_controllers.Count - 1);
        }
        Save();
        if (isNew)
        {
            _log($"[announce] remembered controller address {ip}");
            Wake();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Wake();

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a send is already pending */ }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            await SendAllAsync(ct);
            try
            {
                if (await _wake.WaitAsync(_options.Interval, ct))
                    await Task.Delay(TimeSpan.FromSeconds(2), ct); // let a burst of address changes settle
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Sends one signed announce to every remembered controller (public for tests).</summary>
    public async Task SendAllAsync(CancellationToken ct = default)
    {
        List<string> targets;
        lock (_lock) targets = _controllers.Select(c => c.Address).ToList();
        if (targets.Count == 0) return;

        var addresses = RmsNetwork.GetLocalAddresses();
        foreach (var target in targets)
        {
            if (!IPAddress.TryParse(target, out var ip)) continue;
            try
            {
                var packet = AgentAnnounce.Build(_cert, Environment.MachineName, _grpcPort, addresses, DateTime.UtcNow);
                using var udp = new UdpClient(ip.AddressFamily);
                await udp.SendAsync(packet, new IPEndPoint(ip, _options.TargetPort), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is SocketException or System.Security.Cryptography.CryptographicException)
            {
                _log($"[announce] could not announce to {target}: {ex.Message}");
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var list = JsonSerializer.Deserialize<List<KnownController>>(File.ReadAllText(_storePath)) ?? [];
            lock (_lock)
            {
                _controllers.Clear();
                _controllers.AddRange(list.Where(c => IPAddress.TryParse(c.Address, out _)).Take(Math.Max(1, _options.MaxControllers)));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log($"[announce] could not read {_storePath}: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_controllers);
            AtomicFile.WriteAllText(_storePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"[announce] could not save {_storePath}: {ex.Message}");
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
