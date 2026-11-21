using System.Collections.Concurrent;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Services;

/// <summary>Raised when an agent refuses to pair with (or is not paired with) this controller.</summary>
public sealed class AgentPairingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Turns gRPC / pairing failures into short, actionable operator messages.</summary>
public static class AgentErrors
{
    public const string TwoFactorRequiredDetail = "2FA_REQUIRED";

    public static bool IsPermissionDenied(Exception ex) =>
        ex is RpcException { StatusCode: StatusCode.PermissionDenied } || ex is AgentPairingException;

    public static string Describe(Exception ex)
    {
        switch (ex)
        {
            case AgentPairingException:
                return ex.Message;
            case OperationCanceledException:
                return "Cancelled.";
            case RpcException rpc:
                var detail = string.IsNullOrWhiteSpace(rpc.Status.Detail) ? rpc.StatusCode.ToString() : rpc.Status.Detail;
                return rpc.StatusCode switch
                {
                    StatusCode.PermissionDenied =>
                        $"Permission denied by the agent: {detail} Pair this controller from Endpoints > Connect (approve on the device or enter its 2FA code).",
                    StatusCode.Unauthenticated when detail == TwoFactorRequiredDetail =>
                        "The agent requires 2FA pairing. Use Endpoints > Connect once and enter the 6-digit code shown on the device.",
                    StatusCode.Unauthenticated => $"Not authenticated: {detail}",
                    StatusCode.Unavailable => "Agent unreachable (offline, firewalled or TLS handshake failed).",
                    StatusCode.DeadlineExceeded => "The agent did not answer in time.",
                    StatusCode.Cancelled => "Cancelled.",
                    StatusCode.Unimplemented => "This agent version does not support the operation (update the agent).",
                    _ => detail
                };
            default:
                return ex.Message.Split('\n')[0];
        }
    }
}

/// <summary>
/// Shared, per-endpoint mTLS connections for the feature views (file manager, services, scripts,
/// telemetry, power). Before the first privileged RPC on a connection the pool calls
/// <c>GetAgentInfo</c>, which is the agent's pairing handshake: an unpaired agent either asks for
/// on-device consent or answers <c>2FA_REQUIRED</c>. Every other RPC is refused with
/// PERMISSION_DENIED until the controller is pinned.
/// </summary>
public sealed class AgentConnectionPool : IDisposable
{
    private sealed class Entry(RmsConnection conn)
    {
        public RmsConnection Conn { get; } = conn;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AgentInfo? Info { get; set; }
        public bool Reported { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    /// <summary>
    /// UI callback used for interactive 2FA pairing: receives "host:port", returns the 6-digit code
    /// or null when the operator cancels. Calls are serialized.
    /// </summary>
    public Func<string, Task<string?>>? TwoFactorPrompt { get; set; }

    private static string Key(string host, int port) => $"{host}:{port}";

    /// <summary>Returns the cached connection without any pairing handshake (for streams that must not prompt).</summary>
    public RmsConnection Get(string host, int port) =>
        _entries.GetOrAdd(Key(host, port), _ => new Entry(new RmsConnection(host, port))).Conn;

    public RmsConnection Get(MachineProfile m) => Get(m.DialHost, m.Port);

    public AgentInfo? GetCachedInfo(MachineProfile m) =>
        _entries.TryGetValue(Key(m.DialHost, m.Port), out var e) ? e.Info : null;

    /// <summary>
    /// Called once when a connection to a profile fails with a transport error: re-resolves the endpoint
    /// (name resolution, other known addresses, LAN discovery), updates the profile and returns true when
    /// its dial address changed. The pool then drops the old connection and retries once.
    /// </summary>
    public Func<MachineProfile, CancellationToken, Task<bool>>? Relocate { get; set; }

    /// <summary>Raised after a successful pairing handshake for a profile (computer name, address, certificate).</summary>
    public Action<MachineProfile, RmsConnection, AgentInfo>? Paired { get; set; }

    public async Task<RmsConnection> GetPairedAsync(MachineProfile m, bool interactive, CancellationToken ct = default)
    {
        var host = m.DialHost;
        var port = m.Port;
        RmsConnection conn;
        try
        {
            conn = await GetPairedAsync(host, port, interactive, ct);
        }
        catch (Exception ex) when (Relocate is not null && EndpointLocator.IsTransportError(ex) && !ct.IsCancellationRequested)
        {
            if (!await Relocate(m, ct)) throw;
            Evict(host, port);
            conn = await GetPairedAsync(m.DialHost, m.Port, interactive, ct);
        }
        if (_entries.TryGetValue(Key(m.DialHost, m.Port), out var e) && e.Info is { } info && !e.Reported)
        {
            e.Reported = true;
            try { Paired?.Invoke(m, conn, info); }
            catch (Exception cbEx) { LogBus.Publish(LogBus.Activity, "CONNECT", $"Could not record agent details for {m.DisplayTitle}: {cbEx.Message}", Themes.PaletteTone.Warning); }
        }
        return conn;
    }

    /// <summary>
    /// Returns a connection whose controller certificate is known to be pinned by the agent.
    /// Throws <see cref="AgentPairingException"/> with an operator-facing message otherwise.
    /// </summary>
    public async Task<RmsConnection> GetPairedAsync(string host, int port, bool interactive, CancellationToken ct = default)
    {
        var entry = _entries.GetOrAdd(Key(host, port), _ => new Entry(new RmsConnection(host, port)));
        if (entry.Info is not null) return entry.Conn;

        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Info is not null) return entry.Conn;
            entry.Info = await PairAsync(entry.Conn, host, port, interactive, TwoFactorPrompt, _promptGate, ct);
            return entry.Conn;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Performs the GetAgentInfo pairing handshake on an existing connection (also used by the
    /// Central Hub relay). On-device consent can take a while, hence the generous deadline.
    /// </summary>
    public static async Task<AgentInfo> PairAsync(
        RmsConnection conn, string host, int port, bool interactive,
        Func<string, Task<string?>>? prompt, SemaphoreSlim? promptGate, CancellationToken ct)
    {
        try
        {
            return await conn.Client.GetAgentInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(75), cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated && ex.Status.Detail == AgentErrors.TwoFactorRequiredDetail)
        {
            if (!interactive || prompt is null)
                throw new AgentPairingException(
                    $"{host}:{port} requires 2FA pairing before it accepts commands. Use Endpoints > Connect once and enter the 6-digit code shown on the device.", ex);

            if (promptGate is not null) await promptGate.WaitAsync(ct);
            string? code;
            try { code = await prompt($"{host}:{port}"); }
            finally { promptGate?.Release(); }

            if (string.IsNullOrWhiteSpace(code))
                throw new AgentPairingException($"2FA pairing with {host}:{port} was cancelled.", ex);

            var res = await conn.Client.PairWith2FaAsync(
                new PairRequest { TotpCode = code.Trim(), ControllerName = Environment.MachineName },
                deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);
            if (!res.Success)
                throw new AgentPairingException($"2FA pairing with {host}:{port} failed: {res.Message}");

            return await conn.Client.GetAgentInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            throw new AgentPairingException(
                $"{host}:{port} refused to pair with this controller: {ex.Status.Detail}", ex);
        }
    }

    /// <summary>Forget the pairing state (e.g. after a PERMISSION_DENIED) so the next call re-handshakes.</summary>
    public void MarkUnpaired(string host, int port)
    {
        if (_entries.TryGetValue(Key(host, port), out var e)) e.Info = null;
    }

    public void MarkUnpaired(MachineProfile m) => MarkUnpaired(m.DialHost, m.Port);

    public void Evict(string host, int port)
    {
        if (_entries.TryRemove(Key(host, port), out var e)) e.Conn.Dispose();
    }

    /// <summary>Runs <paramref name="call"/> on a paired connection; resets the pairing cache on PERMISSION_DENIED.</summary>
    public async Task<T> CallAsync<T>(MachineProfile m, bool interactive, Func<RemoteManagement.RemoteManagementClient, Task<T>> call, CancellationToken ct = default)
    {
        var conn = await GetPairedAsync(m, interactive, ct);
        try
        {
            return await call(conn.Client);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            MarkUnpaired(m);
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var e in _entries.Values) e.Conn.Dispose();
        _entries.Clear();
    }
}
