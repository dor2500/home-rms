using System.Windows.Threading;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Services;

/// <summary>
/// Keeps one <c>WatchAgentEvents</c> stream per online/connected agent endpoint, forwarding
/// <see cref="BlockedSiteAttempt"/> and <see cref="AgentWarning"/> events to
/// <see cref="AlertCenterService"/> and the audit/live console log.
/// </summary>
public sealed class AgentEventWatcher : IDisposable
{
    private readonly AgentConnectionPool _pool;
    private readonly AlertCenterService _alerts;
    private readonly Action<string, string> _log;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<MachineProfile, CancellationTokenSource> _running = new();
    private bool _disposed;

    public AgentEventWatcher(
        AgentConnectionPool pool,
        AlertCenterService alerts,
        Action<string, string> log,
        Dispatcher dispatcher)
    {
        _pool = pool;
        _alerts = alerts;
        _log = log;
        _dispatcher = dispatcher;
    }

    public void Sync(IEnumerable<MachineProfile> machines)
    {
        if (_disposed) return;
        var wanted = machines
            .Where(m => m.IsAgentEndpoint && (m.IsOnline || m.IsConnected))
            .ToHashSet();

        foreach (var (m, cts) in _running.ToList())
        {
            if (wanted.Contains(m)) continue;
            cts.Cancel();
            cts.Dispose();
            _running.Remove(m);
        }

        foreach (var m in wanted)
        {
            if (_running.ContainsKey(m)) continue;
            var cts = new CancellationTokenSource();
            _running[m] = cts;
            _ = RunAsync(m, cts.Token);
        }
    }

    private async Task RunAsync(MachineProfile m, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested && !_disposed)
        {
            var received = false;
            try
            {
                var conn = _pool.Get(m);
                using var call = conn.Client.WatchAgentEvents(new Empty(), cancellationToken: ct);
                await foreach (var ev in call.ResponseStream.ReadAllAsync(ct))
                {
                    received = true;
                    backoff = TimeSpan.FromSeconds(2);
                    if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) break;
                    HandleEvent(m, ev);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) { break; }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested) { break; }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated)
            {
                backoff = TimeSpan.FromMinutes(2);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // Older agent without WatchAgentEvents.
                backoff = TimeSpan.FromMinutes(15);
            }
            catch (Exception ex) when (EndpointLocator.IsTransportError(ex) && _pool.Relocate is { } relocate && !ct.IsCancellationRequested)
            {
                try
                {
                    if (await relocate(m, ct)) continue;
                }
                catch (OperationCanceledException) { break; }
            }
            catch
            {
                // Transient error; retry with backoff.
            }

            if (!received && backoff < TimeSpan.FromMinutes(2))
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void HandleEvent(MachineProfile m, AgentEvent ev)
    {
        switch (ev.KindCase)
        {
            case AgentEvent.KindOneofCase.BlockedSite:
                var hit = ev.BlockedSite;
                var endpoint = string.IsNullOrWhiteSpace(hit.EndpointName) ? m.DisplayTitle : hit.EndpointName;
                var proto = string.IsNullOrWhiteSpace(hit.Protocol) ? "HTTPS" : hit.Protocol;
                _alerts.Raise(
                    endpoint,
                    m.Host,
                    AlertSeverity.Warning,
                    $"Blocked site: {hit.Domain}",
                    $"{proto} connection attempt to {hit.Domain} on {endpoint}",
                    cooldownMinutes: 0,
                    category: AlertCategory.BlockedSite);
                _log("ALERT", $"[{endpoint}] Blocked {proto} attempt to {hit.Domain}");
                break;

            case AgentEvent.KindOneofCase.Warning:
                var w = ev.Warning;
                var src = string.IsNullOrWhiteSpace(w.Source) ? "agent" : w.Source;
                _alerts.Raise(
                    m.DisplayTitle,
                    m.Host,
                    AlertSeverity.Warning,
                    $"Agent warning ({src})",
                    w.Message,
                    cooldownMinutes: 1,
                    category: AlertCategory.AgentWarning);
                _log("ALERT", $"[{m.DisplayTitle}] {src}: {w.Message}");
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in _running.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _running.Clear();
    }
}
