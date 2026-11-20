using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Core.Interceptors;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Shared.Server;

/// <summary>Agent-side security settings shared by the Windows and Linux agents.</summary>
public sealed class AgentSecurityOptions
{
    /// <summary>
    /// Pinned-controller store file. <c>null</c> = <see cref="TrustedControllerStore.DefaultStorePath"/>.
    /// Tests point this at a temp file so they never touch (or depend on) the real pin store.
    /// </summary>
    public string? TrustedStorePath { get; init; }

    /// <summary>Failed PairWith2Fa attempts allowed per peer address inside <see cref="PairingWindow"/>.</summary>
    public int MaxPairingFailures { get; init; } = 5;

    public TimeSpan PairingWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a peer is locked out of PairWith2Fa once it exceeded the failure budget.</summary>
    public TimeSpan PairingLockout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Answers "is this caller a pinned controller?" by SHA-256 fingerprint. The pin file is
/// re-read only when it changes on disk, so the check is cheap enough to run on every RPC.
/// </summary>
public sealed class PinnedControllerAuthorizer(AgentSecurityOptions options, Action<string> log)
{
    private readonly object _gate = new();
    private (DateTime Stamp, long Length, HashSet<string> Fingerprints)? _cache;

    public string? StorePath => options.TrustedStorePath;
    public Action<string> Log => log;

    public bool HasAnyPinned => Current().Count > 0;

    public bool IsPinned(X509Certificate2? cert)
    {
        if (cert is null) return false;
        var fp = ControllerIdentity.NormalizeFingerprint(ControllerIdentity.GetSha256Fingerprint(cert));
        return Current().Contains(fp);
    }

    public void Pin(X509Certificate2 cert, string label)
    {
        TrustedControllerStore.AddPinned(ControllerIdentity.GetSha256Fingerprint(cert), label, options.TrustedStorePath);
        Invalidate();
    }

    public void Invalidate()
    {
        lock (_gate) _cache = null;
    }

    private HashSet<string> Current()
    {
        var path = options.TrustedStorePath ?? TrustedControllerStore.DefaultStorePath;
        DateTime stamp;
        long length;
        try
        {
            var fi = new FileInfo(path);
            stamp = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
            length = fi.Exists ? fi.Length : -1;
        }
        catch
        {
            stamp = DateTime.MinValue;
            length = -1;
        }

        lock (_gate)
        {
            if (_cache is { } c && c.Stamp == stamp && c.Length == length)
                return c.Fingerprints;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in TrustedControllerStore.Load(path))
                set.Add(ControllerIdentity.NormalizeFingerprint(e.Sha256));
            _cache = (stamp, length, set);
            return set;
        }
    }
}

/// <summary>
/// Server interceptor: every RPC except GetAgentInfo (consent pairing) and PairWith2Fa (TOTP
/// pairing) requires the caller's client certificate to be a pinned controller. The TLS layer
/// lets unpinned controller certificates through while nothing is pinned yet (so they can pair);
/// this closes the gap where such a caller could invoke Shell / DeployPackage / SystemOp directly.
/// </summary>
public sealed class PinnedControllerInterceptor(PinnedControllerAuthorizer auth) : Interceptor
{
    private static readonly string ServicePrefix = "/" + RemoteManagement.Descriptor.FullName + "/";

    private static readonly HashSet<string> PairingMethods = new(StringComparer.Ordinal)
    {
        ServicePrefix + "GetAgentInfo",
        ServicePrefix + "PairWith2Fa",
    };

    public static bool IsPairingMethod(string method) => PairingMethods.Contains(method);

    private void Authorize(ServerCallContext context)
    {
        if (PairingMethods.Contains(context.Method)) return;

        var cert = context.GetHttpContext().Connection.ClientCertificate;
        if (auth.IsPinned(cert)) return;

        var who = cert?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "unknown";
        var method = context.Method.StartsWith(ServicePrefix, StringComparison.Ordinal)
            ? context.Method[ServicePrefix.Length..]
            : context.Method;
        auth.Log($"[security] DENIED {method} from unpaired controller '{who}'");

        var message = auth.HasAnyPinned
            ? $"Controller '{who}' is not paired with this agent (certificate fingerprint is not pinned)."
            : $"Controller '{who}' is not paired with this agent yet. Call GetAgentInfo (approve on the device) or PairWith2Fa first.";
        throw new RpcException(new Status(StatusCode.PermissionDenied, message));
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, responseStream, context);
    }
}

/// <summary>Sliding-window failure counter with lockout, keyed by peer address.</summary>
public sealed class PairingRateLimiter(AgentSecurityOptions options)
{
    private sealed class Entry
    {
        public readonly Queue<DateTimeOffset> Failures = new();
        public DateTimeOffset LockedUntil = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the remaining lockout time, or null when the peer may attempt pairing.</summary>
    public TimeSpan? GetLockout(string peer)
    {
        if (!_entries.TryGetValue(peer, out var e)) return null;
        lock (e)
        {
            var now = DateTimeOffset.UtcNow;
            return e.LockedUntil > now ? e.LockedUntil - now : null;
        }
    }

    /// <summary>Records a failed attempt. Returns true when this failure triggered a lockout.</summary>
    public bool RecordFailure(string peer)
    {
        var e = _entries.GetOrAdd(peer, _ => new Entry());
        lock (e)
        {
            var now = DateTimeOffset.UtcNow;
            while (e.Failures.Count > 0 && now - e.Failures.Peek() > options.PairingWindow)
                e.Failures.Dequeue();
            e.Failures.Enqueue(now);
            if (e.Failures.Count < options.MaxPairingFailures) return false;
            e.Failures.Clear();
            e.LockedUntil = now + options.PairingLockout;
            return true;
        }
    }

    public void RecordSuccess(string peer) => _entries.TryRemove(peer, out _);
}

/// <summary>
/// Pairing logic shared by both agents: consent-based pinning on GetAgentInfo and
/// rate-limited TOTP pinning via PairWith2Fa.
/// </summary>
public sealed class ControllerPairing(PinnedControllerAuthorizer auth, PairingRateLimiter limiter)
{
    public static string OperatorOf(ServerCallContext ctx)
    {
        var cert = ctx.GetHttpContext().Connection.ClientCertificate;
        return cert?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "unknown";
    }

    private static string PeerKey(ServerCallContext ctx) =>
        ctx.GetHttpContext().Connection.RemoteIpAddress?.ToString() ?? ctx.Peer ?? "unknown";

    /// <summary>
    /// Called from GetAgentInfo. When no controller is pinned yet, the caller (which already passed
    /// the TLS handshake) is pinned after explicit on-device consent, or told to use 2FA pairing when
    /// the agent requires it. Throws an <see cref="RpcException"/> when pairing is not granted.
    /// </summary>
    public void EnsurePairedViaConsent(ServerCallContext context, Func<string, string, bool> requestGrant)
    {
        var cert = context.GetHttpContext().Connection.ClientCertificate;
        if (cert is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "No client certificate presented."));
        if (auth.IsPinned(cert)) return;

        var op = OperatorOf(context);
        if (auth.HasAnyPinned)
        {
            // Normally unreachable: the TLS layer rejects unpinned certificates once a pin exists.
            auth.Log($"[security] '{op}' is not the pinned controller");
            throw new RpcException(new Status(StatusCode.PermissionDenied, "This controller is not paired with the agent."));
        }

        var cfg = TwoFactorStore.LoadAgent();
        if (cfg.RequireOnStartupOrPair)
        {
            auth.Log($"[security] '{op}' requires initial 2FA pairing");
            throw new RpcException(new Status(StatusCode.Unauthenticated, "2FA_REQUIRED"));
        }

        var fp = ControllerIdentity.GetSha256Fingerprint(cert);
        if (!requestGrant(op, $"pair & permanently pin this Controller\n({fp[..23]}...)"))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Controller pairing was declined."));

        auth.Pin(cert, op);
        auth.Log($"[security] pinned Controller '{op}' ({fp[..23]}...)");
    }

    public PairResult PairWith2Fa(PairRequest request, ServerCallContext context)
    {
        var cert = context.GetHttpContext().Connection.ClientCertificate;
        var op = OperatorOf(context);
        var peer = PeerKey(context);

        if (limiter.GetLockout(peer) is { } wait)
        {
            auth.Log($"[security] 2FA pairing from '{op}' ({peer}) refused: locked out for {Math.Ceiling(wait.TotalMinutes)} more min");
            return new PairResult
            {
                Success = false,
                Message = $"Too many failed 2FA attempts. Try again in {Math.Ceiling(wait.TotalMinutes)} minute(s)."
            };
        }

        if (cert is null)
            return new PairResult { Success = false, Message = "No client certificate presented." };

        var cfg = TwoFactorStore.LoadAgent();
        if (!TotpHelper.VerifyCode(cfg.TotpSecret, request.TotpCode))
        {
            var locked = limiter.RecordFailure(peer);
            auth.Log($"[security] FAILED 2FA pairing attempt from '{op}' ({peer}){(locked ? " — peer locked out" : "")}");
            return new PairResult
            {
                Success = false,
                Message = locked
                    ? "Invalid 6-digit 2FA code. Too many failed attempts — pairing is locked for a while."
                    : "Invalid 6-digit 2FA code."
            };
        }

        limiter.RecordSuccess(peer);
        var fp = ControllerIdentity.GetSha256Fingerprint(cert);
        var label = string.IsNullOrWhiteSpace(request.ControllerName) ? op : request.ControllerName.Trim();
        auth.Pin(cert, label.Length <= 64 ? label : label[..64]);
        auth.Log($"[security] 2FA paired & pinned Controller '{op}' ({fp[..23]}...)");
        return new PairResult { Success = true, Message = "Controller paired & pinned." };
    }
}
