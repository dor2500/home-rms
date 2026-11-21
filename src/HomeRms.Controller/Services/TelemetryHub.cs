using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Services;

public readonly record struct TelemetryPoint(
    DateTime TimeUtc, double Cpu, double Ram, double DiskMax, double RxBps, double TxBps);

/// <summary>Rolling 5-minute telemetry buffer + latest values for one agent endpoint (UI-thread owned).</summary>
public sealed class EndpointTelemetry : INotifyPropertyChanged
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly List<TelemetryPoint> _points = new();
    private string _status = "Waiting";
    private bool _isLive;
    private bool _isHot;

    public EndpointTelemetry(MachineProfile profile) => Profile = profile;

    public MachineProfile Profile { get; }
    public string Name => Profile.DisplayTitle;
    public IReadOnlyList<TelemetryPoint> Points => _points;
    public TelemetryPoint? Latest => _points.Count > 0 ? _points[^1] : null;
    public long UptimeSeconds { get; private set; }
    public int ProcessCount { get; private set; }
    public DateTime? LastSampleUtc => Latest?.TimeUtc;

    public string Status { get => _status; set { _status = value; Raise(); } }
    public bool IsLive { get => _isLive; set { _isLive = value; Raise(); } }
    /// <summary>True while the latest sample breaches a CPU / RAM / disk alert threshold.</summary>
    public bool IsHot { get => _isHot; private set { _isHot = value; Raise(); } }

    public double[] CpuSeries { get; private set; } = [];
    public double[] RamSeries { get; private set; } = [];
    public double[] DiskSeries { get; private set; } = [];
    public double[] NetSeries { get; private set; } = [];

    public string CpuText => Latest is { } p ? $"{p.Cpu:0}%" : "--";
    public string RamText => Latest is { } p ? $"{p.Ram:0}%" : "--";
    public string DiskText => Latest is { } p ? $"{p.DiskMax:0}%" : "--";
    public string NetText => Latest is { } p ? $"{FormatRate(p.RxBps)} / {FormatRate(p.TxBps)}" : "--";
    public string UptimeText => UptimeSeconds > 0 ? FormatUptime(UptimeSeconds) : "--";
    public string ProcText => ProcessCount > 0 ? ProcessCount.ToString() : "--";

    internal void Add(TelemetrySample s, AlertThresholds t)
    {
        var memPct = s.MemTotalBytes > 0 ? s.MemUsedBytes * 100.0 / s.MemTotalBytes : 0;
        var diskMax = s.Disks.Where(d => d.TotalBytes > 0)
            .Select(d => (d.TotalBytes - d.FreeBytes) * 100.0 / d.TotalBytes)
            .DefaultIfEmpty(0).Max();
        _points.Add(new TelemetryPoint(DateTime.UtcNow, Math.Clamp(s.CpuPercent, 0, 100), memPct, diskMax,
            s.NetRxBytesPerSec, s.NetTxBytesPerSec));
        var cutoff = DateTime.UtcNow - Window;
        var drop = 0;
        while (drop < _points.Count && _points[drop].TimeUtc < cutoff) drop++;
        if (drop > 0) _points.RemoveRange(0, drop);

        UptimeSeconds = s.UptimeSeconds;
        ProcessCount = s.ProcessCount;
        CpuSeries = _points.Select(p => p.Cpu).ToArray();
        RamSeries = _points.Select(p => p.Ram).ToArray();
        DiskSeries = _points.Select(p => p.DiskMax).ToArray();
        NetSeries = _points.Select(p => p.RxBps + p.TxBps).ToArray();
        RefreshHot(t);
        Raise(string.Empty);
    }

    public void RefreshHot(AlertThresholds t)
    {
        if (Latest is not { } last) { IsHot = false; return; }
        IsHot = last.Cpu >= t.CpuThresholdPercent || last.Ram >= t.RamThresholdPercent ||
                100 - last.DiskMax < t.DiskFreeMinPercent;
    }

    internal void Clear()
    {
        _points.Clear();
        CpuSeries = RamSeries = DiskSeries = NetSeries = [];
        IsHot = false;
        Raise(string.Empty);
    }

    public static string FormatRate(double bps) => bps switch
    {
        >= 1024 * 1024 => $"{bps / 1024 / 1024:0.0} MB/s",
        >= 1024 => $"{bps / 1024:0} KB/s",
        _ => $"{bps:0} B/s"
    };

    public static string FormatUptime(long s)
    {
        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalDays >= 1 ? $"{(int)ts.TotalDays}d {ts.Hours}h" : ts.TotalHours >= 1 ? $"{ts.Hours}h {ts.Minutes}m" : $"{ts.Minutes}m";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// Keeps one GetTelemetry stream per online Windows-agent endpoint (reconnecting with exponential
/// backoff, stopped when the endpoint goes offline or is removed), keeps a rolling 5-minute buffer
/// and feeds real CPU / RAM / disk values into <see cref="AlertCenterService"/>.
/// Streams never trigger the pairing handshake (no surprise consent prompts on remote PCs):
/// an unpaired agent shows "Not paired" until the operator connects to it once.
/// </summary>
public sealed class TelemetryHub : IDisposable
{
    private const int IntervalMs = 2000;

    private readonly AgentConnectionPool _pool;
    private readonly AlertCenterService _alerts;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<MachineProfile, CancellationTokenSource> _running = new();

    public TelemetryHub(AgentConnectionPool pool, AlertCenterService alerts, Dispatcher dispatcher)
    {
        _pool = pool;
        _alerts = alerts;
        _dispatcher = dispatcher;
    }

    /// <summary>One entry per agent endpoint being monitored (UI thread).</summary>
    public ObservableCollection<EndpointTelemetry> Endpoints { get; } = new();

    /// <summary>Raised on the UI thread after every sample (for fleet totals).</summary>
    public event Action? SampleReceived;

    public EndpointTelemetry? Find(MachineProfile m) => Endpoints.FirstOrDefault(e => e.Profile == m);

    /// <summary>Starts / stops streams to match the fleet. Call on the UI thread.</summary>
    public void Sync(IEnumerable<MachineProfile> machines)
    {
        var all = machines.ToHashSet();
        var wanted = all
            .Where(m => (m.Kind == EndpointKind.WindowsAgent || (m.Kind == EndpointKind.Linux && m.ConnectionMode == ConnectionMode.Agent))
                        && (m.IsOnline || m.IsConnected))
            .ToHashSet();

        foreach (var (m, cts) in _running.ToList())
        {
            if (wanted.Contains(m)) continue;
            cts.Cancel();
            _running.Remove(m);
            if (Find(m) is { } et)
            {
                et.IsLive = false;
                et.Status = "Offline";
            }
            if (all.Contains(m))
                _alerts.EvaluateSample(m.DisplayTitle, m.Host, false, 0, 0);
        }

        // Endpoints removed from the fleet disappear from the tiles; offline ones stay (shown offline).
        foreach (var et in Endpoints.Where(e => !all.Contains(e.Profile)).ToList())
            Endpoints.Remove(et);

        foreach (var m in wanted)
        {
            if (_running.ContainsKey(m)) continue;
            var et = Find(m);
            if (et is null)
            {
                et = new EndpointTelemetry(m);
                Endpoints.Add(et);
            }
            var cts = new CancellationTokenSource();
            _running[m] = cts;
            _ = RunAsync(et, cts.Token);
        }
    }

    private async Task RunAsync(EndpointTelemetry et, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            var gotSample = false;
            try
            {
                RunOnUi(() => et.Status = "Connecting");
                var conn = _pool.Get(et.Profile);
                using var call = conn.Client.GetTelemetry(new TelemetryRequest { IntervalMs = IntervalMs }, cancellationToken: ct);
                await foreach (var s in call.ResponseStream.ReadAllAsync(ct))
                {
                    gotSample = true;
                    backoff = TimeSpan.FromSeconds(2);
                    if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) break;
                    await _dispatcher.InvokeAsync(() => OnSample(et, s));
                }
                SetStatus(et, "Stream ended", live: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) { break; }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested) { break; }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated)
            {
                SetStatus(et, "Not paired - connect once to pair", live: false);
                backoff = TimeSpan.FromMinutes(2);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                SetStatus(et, "Agent too old for telemetry", live: false);
                backoff = TimeSpan.FromMinutes(10);
            }
            catch (Exception ex)
            {
                SetStatus(et, AgentErrors.Describe(ex), live: false);
                // The endpoint may have moved: re-resolve once (rate-limited by the relocator) and reconnect at once.
                if (EndpointLocator.IsTransportError(ex) && _pool.Relocate is { } relocate && !ct.IsCancellationRequested)
                {
                    bool moved;
                    try { moved = await relocate(et.Profile, ct); }
                    catch (OperationCanceledException) { break; }
                    if (moved) continue;
                }
            }

            if (!gotSample && backoff < TimeSpan.FromMinutes(2))
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try
        {
            _dispatcher.Invoke(action);
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
    }

    private void SetStatus(EndpointTelemetry et, string status, bool live) =>
        RunOnUi(() =>
        {
            et.Status = status;
            et.IsLive = live;
        });

    private void OnSample(EndpointTelemetry et, TelemetrySample s)
    {
        et.Add(s, _alerts.Thresholds);
        et.IsLive = true;
        et.Status = "Live";
        if (et.Latest is not { } p) return;

        var m = et.Profile;
        m.CpuPercent = p.Cpu;
        m.RamPercent = p.Ram;
        _alerts.EvaluateSample(m.DisplayTitle, m.Host, true, p.Cpu, p.Ram, minDiskFreePercent: 100 - p.DiskMax);
        SampleReceived?.Invoke();
    }

    public void Dispose()
    {
        foreach (var cts in _running.Values) cts.Cancel();
        _running.Clear();
    }
}
