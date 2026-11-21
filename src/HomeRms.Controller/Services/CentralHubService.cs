using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HomeRms.Controller.Models;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Controller.Services;

/// <summary>
/// Central Home RMS Server (Hub, Relay, Reverse Proxy &amp; browser Web Console, see CentralHubService.Web.cs).
/// All remote clients and the Mobile Web App communicate strictly with this Central Server;
/// mTLS certificates, DPAPI credentials, and SSH keys remain strictly on the server.
/// <para>
/// Security model (plain HTTP on the LAN): every route except the page itself and the sign-in API
/// requires a session cookie obtained with the hub access key or a controller TOTP / backup code
/// (unless the operator explicitly disabled sign-in), state-changing requests must carry the
/// <c>X-HomeRms-Request</c> header (blocks cross-site form posts), and the relay / reverse proxy
/// only reach endpoints that are saved in the fleet - never arbitrary hosts.
/// </para>
/// </summary>
public sealed partial class CentralHubService : IDisposable
{
    public const int DefaultHubPort = 7480;

    private readonly Func<IReadOnlyList<MachineProfile>> _getEndpoints;
    private readonly Func<Task> _triggerLanScan;
    private readonly Action<string, string> _logEvent;
    private readonly AgentlessService _agentless;
    private readonly SshApplianceService _ssh;
    private readonly AlertCenterService _alerts;

    private readonly ConcurrentDictionary<string, RmsConnection> _connectionPool = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentInfo> _pairedInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _mobileSessions = new();
    private readonly HttpClient _proxyClient;
    private readonly HubRelaySettings _settings;

    public const string CsrfHeader = "X-HomeRms-Request";
    private const int MaxSessions = 256;
    private const int MaxBodyBytes = 1024 * 1024;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _relayedRequests;

    public int Port { get; private set; } = DefaultHubPort;
    public bool IsRunning { get; private set; }
    public bool LanBound { get; private set; }
    public long RelayedRequests => Interlocked.Read(ref _relayedRequests);

    public CentralHubService(
        Func<IReadOnlyList<MachineProfile>> getEndpoints,
        Func<Task> triggerLanScan,
        Action<string, string> logEvent,
        AgentlessService? agentless = null,
        SshApplianceService? ssh = null,
        AlertCenterService? alerts = null,
        HubRelaySettings? settings = null)
    {
        _getEndpoints = getEndpoints;
        _triggerLanScan = triggerLanScan;
        _logEvent = logEvent;
        _agentless = agentless ?? new AgentlessService();
        _ssh = ssh ?? new SshApplianceService();
        _alerts = alerts ?? new AlertCenterService();
        // Tests / scratch harnesses pass in-memory settings so the user's hub-relay.json is never touched.
        _settings = settings ?? HubRelaySettings.Load();

        // Appliances (Home Assistant, NAS...) typically use self-signed certificates, so an invalid
        // certificate is tolerated ONLY for hosts that are saved endpoints with a web UI. Redirects are
        // not followed, so a proxied page can never bounce the hub to another host.
        _proxyClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (req, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None ||
                (req.RequestUri is { } u && FindWebEndpoint(u.Host) is not null),
        })
        { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>Current relay settings (sign-in requirement, LAN binding, access key).</summary>
    public HubRelaySettings Settings => _settings;

    /// <summary>True when callers must sign in (always, unless explicitly disabled and console 2FA is off).</summary>
    public bool AuthRequired
    {
        get
        {
            if (_settings.RequireAuth) return true;
            try { return TwoFactorStore.LoadController().RequireOnStartupOrPair; }
            catch { return true; } // unreadable security config: fail closed
        }
    }

    public string? AccessKey => _settings.TryGetAccessKey();

    /// <summary>Generates a new access key and signs out every hub session.</summary>
    public string RegenerateAccessKey()
    {
        var key = HubRelaySettings.GenerateAccessKey();
        _settings.SetAccessKey(key);
        _settings.Save();
        _mobileSessions.Clear();
        _logEvent("HUB", "Hub access key regenerated; all mobile sessions were signed out.");
        return key;
    }

    public void SetRequireAuth(bool required)
    {
        if (_settings.RequireAuth == required) return;
        _settings.RequireAuth = required;
        _settings.Save();
        _logEvent("HUB", required
            ? "Hub relay sign-in is now required (access key or 2FA code)."
            : "WARNING: hub relay sign-in was turned OFF. Anyone who can reach this PC on the LAN can use the relay.");
    }

    public void Start(int preferredPort = DefaultHubPort)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();

        foreach (var candidate in new[] { preferredPort, preferredPort + 1, preferredPort + 10 })
        {
            if ((_settings.AllowLan && TryStartOn($"http://+:{candidate}/", candidate, lan: true)) ||
                TryStartOn($"http://localhost:{candidate}/", candidate, lan: false))
            {
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
                _logEvent("HUB", $"Central Hub & Relay listening on {(LanBound ? $"0.0.0.0:{Port}" : $"localhost:{Port}")}" +
                                 (AuthRequired ? " (sign-in required: hub access key or 2FA code)" : " WITHOUT sign-in (explicitly disabled)"));
                return;
            }
        }

        _logEvent("HUB", "Could not bind Central Hub HTTP listener (port unavailable).");
    }

    private bool TryStartOn(string prefix, int port, bool lan)
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add(prefix);
            l.Start();
            _listener = l;
            Port = port;
            IsRunning = true;
            LanBound = lan;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public RmsConnection GetOrCreateConnection(string host, int port)
    {
        var key = $"{host}:{port}";
        if (_connectionPool.TryGetValue(key, out var existing))
            return existing;

        // Requests run in parallel: never let two racing callers both create (and leak) a channel.
        lock (_connectionPool)
        {
            if (_connectionPool.TryGetValue(key, out existing))
                return existing;
            var conn = new RmsConnection(host, port);
            _connectionPool[key] = conn;
            return conn;
        }
    }

    public void EvictConnection(string host, int port)
    {
        var key = $"{host}:{port}";
        _pairedInfo.TryRemove(key, out _);
        if (_connectionPool.TryRemove(key, out var conn))
            conn.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequestAsync(ctx, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex) { _logEvent("HUB", $"Listener error: {ex.Message}"); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _relayedRequests);
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        try
        {
            // CORS / security headers for local mobile web app
            ctx.Response.Headers["X-HomeRms-Relay"] = "CentralHub/2.0";

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";

            // Web console single-page app (static, no data inside: everything comes from the signed-in API).
            if (await TryServeAssetAsync(ctx, path))
                return;

            // Cross-site form posts cannot set custom headers (that needs a CORS preflight, which the
            // hub never approves), so this blocks CSRF against every state-changing hub route.
            if (method is not ("GET" or "HEAD") && ctx.Request.Headers[CsrfHeader] != "1" &&
                !path.StartsWith("/proxy/", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = 403;
                await WriteJsonAsync(ctx.Response, new { error = $"Missing {CsrfHeader} header." });
                return;
            }

            var authRequired = AuthRequired;
            var authed = !authRequired || IsMobileAuthed(ctx.Request);

            if (path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx.Response, new { required = authRequired, authenticated = authed });
                return;
            }

            if (path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                if (ctx.Request.Cookies["hrms_session"]?.Value is { Length: > 0 } old) _mobileSessions.TryRemove(old, out _);
                ctx.Response.Headers["Set-Cookie"] = "hrms_session=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0";
                await WriteJsonAsync(ctx.Response, new { ok = true });
                return;
            }

            if (path.Equals("/api/auth/verify", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                var body = await ReadBodyJsonAsync(ctx.Request);
                var code = GetString(body, "code");
                var remember = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("remember", out var rEl) && rEl.ValueKind == JsonValueKind.True;
                if (string.IsNullOrWhiteSpace(code))
                {
                    // Not an attempt: do not count it against the shared unlock throttle.
                    ctx.Response.StatusCode = 400;
                    await WriteJsonAsync(ctx.Response, new { ok = false, error = "Enter the hub access key or a 2FA code." });
                    return;
                }
                var cfg = TwoFactorStore.LoadController();
                // Shared persistent attempt throttle (same counter as the console lock overlay).
                var wait = TwoFactorStore.GetUnlockDelayRemaining(cfg);
                if (wait > TimeSpan.Zero)
                {
                    ctx.Response.StatusCode = 429;
                    ctx.Response.Headers["Retry-After"] = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
                    await WriteJsonAsync(ctx.Response, new { ok = false, error = $"Too many failed attempts. Try again in {(int)Math.Ceiling(wait.TotalSeconds)} seconds." });
                    return;
                }
                var keyOk = HubRelaySettings.KeyMatches(AccessKey, code);
                var updated = cfg;
                var usedBackup = false;
                if (keyOk || TwoFactorStore.VerifyCodeOrBackup(cfg, code, out updated, out usedBackup))
                {
                    var afterSuccess = TwoFactorStore.RegisterUnlockSuccess(updated);
                    if (usedBackup || !ReferenceEquals(afterSuccess, updated)) TwoFactorStore.SaveController(afterSuccess);
                    PruneSessions();
                    var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                    var days = remember ? Math.Clamp(cfg.RememberDays, 1, 365) : 1;
                    _mobileSessions[token] = DateTimeOffset.UtcNow.AddDays(days);
                    ctx.Response.Headers["Set-Cookie"] = $"hrms_session={token}; Path=/; HttpOnly; SameSite=Strict; Max-Age={days * 86400}";
                    _logEvent("HUB", $"Mobile hub sign-in from {ctx.Request.RemoteEndPoint?.Address} ({(keyOk ? "access key" : usedBackup ? "backup code" : "TOTP")}).");
                    await WriteJsonAsync(ctx.Response, new { ok = true });
                }
                else
                {
                    var failed = TwoFactorStore.RegisterUnlockFailure(cfg);
                    TwoFactorStore.SaveController(failed);
                    TwoFactorStore.LogSecurityEvent($"Mobile hub sign-in failed from {ctx.Request.RemoteEndPoint?.Address}; consecutive failures: {failed.FailedUnlockAttempts}");
                    ctx.Response.StatusCode = 401;
                    await WriteJsonAsync(ctx.Response, new { ok = false, error = "Invalid access key, 2FA or backup code" });
                }
                return;
            }

            // Everything past this point (API, relay AND reverse proxy) requires a signed-in session.
            if (!authed)
            {
                ctx.Response.StatusCode = 401;
                await WriteJsonAsync(ctx.Response, new { error = "Sign-in required (hub access key or 2FA code). Open the hub page to sign in." });
                return;
            }

            // Web Console API (sessions, CSRF and saved-endpoint-only access as above).
            if (path.StartsWith("/api/v2/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleV2Async(ctx, path, method, ct);
                return;
            }

            if (path.Equals("/api/hub/status", StringComparison.OrdinalIgnoreCase))
            {
                var eps = _getEndpoints();
                await WriteJsonAsync(ctx.Response, new
                {
                    status = "Online",
                    port = Port,
                    lanBound = LanBound,
                    pooledConnections = _connectionPool.Count,
                    relayedRequests = RelayedRequests,
                    endpointsTotal = eps.Count,
                    endpointsOnline = eps.Count(e => e.IsOnline || e.IsConnected),
                    unreadAlerts = _alerts.UnreadCount,
                    authRequired,
                });
                return;
            }

            if (path.Equals("/api/endpoints", StringComparison.OrdinalIgnoreCase))
            {
                var eps = _getEndpoints().Select(e => new
                {
                    name = e.DisplayTitle,
                    host = e.Host,
                    port = e.Port,
                    kind = e.Kind.ToString(),
                    modeBadge = e.ModeBadge,
                    os = e.OsTag,
                    online = e.IsOnline,
                    connected = e.IsConnected,
                    cpu = Math.Round(e.CpuPercent, 1),
                    ram = Math.Round(e.RamPercent, 1),
                    latencyMs = e.LatencyMs,
                    webUrl = e.WebUrl,
                });
                await WriteJsonAsync(ctx.Response, eps);
                return;
            }

            if (path.Equals("/api/endpoints/scan", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                _ = _triggerLanScan();
                await WriteJsonAsync(ctx.Response, new { started = true });
                return;
            }

            if (path.Equals("/api/alerts", StringComparison.OrdinalIgnoreCase))
            {
                var list = _alerts.Alerts.Take(50).Select(a => new
                {
                    id = a.Id,
                    endpoint = a.EndpointName,
                    host = a.Host,
                    severity = a.SeverityText,
                    title = a.Title,
                    detail = a.Detail,
                    time = a.TimeText,
                    acknowledged = a.Acknowledged,
                });
                await WriteJsonAsync(ctx.Response, new { unread = _alerts.UnreadCount, alerts = list });
                return;
            }

            if (path.Equals("/api/alerts/ack", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                var body = await ReadBodyJsonAsync(ctx.Request);
                var id = GetString(body, "id");
                if (string.IsNullOrWhiteSpace(id)) _alerts.AcknowledgeAll();
                else _alerts.Acknowledge(id);
                await WriteJsonAsync(ctx.Response, new { ok = true, unread = _alerts.UnreadCount });
                return;
            }

            // Reverse Proxy for Home Assistant, NAS, and local Web Services: /proxy/{host}/{subpath}
            if (path.StartsWith("/proxy/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleReverseProxyAsync(ctx, path["/proxy/".Length..], ct);
                return;
            }

            // Relay endpoints: /api/relay/{host}:{port}/{action}
            if (path.StartsWith("/api/relay/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = path["/api/relay/".Length..];
                var slash = rest.IndexOf('/');
                if (slash > 0)
                {
                    var target = rest[..slash];
                    var action = rest[(slash + 1)..];
                    await HandleRelayAsync(ctx, target, action, ct);
                    return;
                }
            }

            ctx.Response.StatusCode = 404;
            await WriteJsonAsync(ctx.Response, new { error = "Route not found" });
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = ex is InvalidDataException ? 413 : 500;
                await WriteJsonAsync(ctx.Response, new { error = ex.Message });
            }
            catch (Exception writeEx)
            {
                // The response was already started / the client went away: nothing more to send.
                _logEvent("HUB", $"{method} {path} failed: {ex.Message} (response not sent: {writeEx.Message})");
                try { ctx.Response.Abort(); } catch { /* already closed */ }
            }
        }
    }

    private async Task HandleReverseProxyAsync(HttpListenerContext ctx, string rest, CancellationToken ct)
    {
        var slash = rest.IndexOf('/');
        var host = slash > 0 ? rest[..slash] : rest;
        var sub = slash > 0 ? rest[slash..] : "/";
        if (!string.IsNullOrEmpty(ctx.Request.Url?.Query)) sub += ctx.Request.Url.Query;

        // Only saved endpoints with a configured web UI can be proxied (never arbitrary hosts), and the
        // final URL must still point at that endpoint's own scheme/host/port.
        var ep = _getEndpoints().FirstOrDefault(e => e.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(e.WebUrl));
        if (ep is null ||
            !Uri.TryCreate(ep.WebUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") ||
            !Uri.TryCreate(ep.WebUrl.Trim().TrimEnd('/') + sub, UriKind.Absolute, out var targetUri) ||
            !string.Equals(targetUri.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 404;
            await WriteJsonAsync(ctx.Response, new { error = $"'{host}' is not a saved endpoint with a web UI address." });
            return;
        }

        using var reqMsg = new HttpRequestMessage(new HttpMethod(ctx.Request.HttpMethod), targetUri);
        if (ctx.Request.HasEntityBody)
        {
            if (ctx.Request.ContentLength64 > MaxBodyBytes * 16L)
            {
                ctx.Response.StatusCode = 413;
                await WriteJsonAsync(ctx.Response, new { error = "Request body too large." });
                return;
            }
            var ms = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(ms, ct);
            ms.Position = 0;
            reqMsg.Content = new StreamContent(ms);
            if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                reqMsg.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
        }

        using var upstream = await _proxyClient.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct);
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Headers.Location is { } location)
            ctx.Response.Headers["Location"] = location.IsAbsoluteUri ? location.AbsoluteUri : location.OriginalString;
        ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "text/html; charset=utf-8";
        var bytes = await upstream.Content.ReadAsByteArrayAsync(ct);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    private async Task HandleRelayAsync(HttpListenerContext ctx, string target, string action, CancellationToken ct)
    {
        var parts = target.Split(':');
        var host = parts[0];
        int? requestedPort = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : null;

        // Only saved endpoints can be relayed to: the hub must never act as an open relay that lends
        // this controller's mTLS identity (or stored WinRM / SSH credentials) to arbitrary hosts.
        var candidates = _getEndpoints().Where(e => e.Host.Equals(host, StringComparison.OrdinalIgnoreCase)).ToList();
        var profile = candidates.FirstOrDefault(e => requestedPort is null || e.Port == requestedPort)
                      ?? candidates.FirstOrDefault(e => e.IsAgentless || e.IsAppliance);
        if (profile is null)
        {
            ctx.Response.StatusCode = 404;
            await WriteJsonAsync(ctx.Response, new { ok = false, error = $"'{target}' is not a saved endpoint." });
            return;
        }
        var port = profile.Port;

        // Route Agentless Windows or Linux/SSH endpoints through server-side adapters
        if (profile is { IsAgentless: true } && action.Equals("shell", StringComparison.OrdinalIgnoreCase))
        {
            var body = await ReadBodyJsonAsync(ctx.Request);
            var cmd = GetString(body, "command");
            var (ok, output) = await _agentless.RunShellAsync(profile, cmd, ct);
            await WriteJsonAsync(ctx.Response, new { ok, output });
            return;
        }
        if (profile is { IsAppliance: true } && action.Equals("shell", StringComparison.OrdinalIgnoreCase))
        {
            var body = await ReadBodyJsonAsync(ctx.Request);
            var cmd = GetString(body, "command");
            var (ok, output) = await _ssh.ExecuteSshAsync(profile, cmd, ct);
            await WriteJsonAsync(ctx.Response, new { ok, output });
            return;
        }

        var conn = GetOrCreateConnection(profile.DialHost, port);
        var key = $"{profile.DialHost}:{port}";

        // The agent refuses every RPC except GetAgentInfo / PairWith2Fa from an unpinned controller,
        // so run the GetAgentInfo pairing handshake first. The relay is headless: a 2FA-only agent
        // cannot be paired from here and gets a clear message instead.
        AgentInfo info;
        try
        {
            if (!_pairedInfo.TryGetValue(key, out info!))
            {
                info = await AgentConnectionPool.PairAsync(conn, host, port, interactive: false, prompt: null, promptGate: null, ct);
                _pairedInfo[key] = info;
            }
        }
        catch (Exception ex) when (ex is AgentPairingException or Grpc.Core.RpcException)
        {
            ctx.Response.StatusCode = AgentErrors.IsPermissionDenied(ex) ? 403 : 502;
            await WriteJsonAsync(ctx.Response, new { ok = false, error = AgentErrors.Describe(ex) });
            return;
        }

        try
        {
            await RelayAgentActionAsync(ctx, conn, info, action, ct);
        }
        catch (Grpc.Core.RpcException ex)
        {
            if (ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied) _pairedInfo.TryRemove(key, out _);
            ctx.Response.StatusCode = ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied ? 403 : 502;
            await WriteJsonAsync(ctx.Response, new { ok = false, error = AgentErrors.Describe(ex) });
        }
    }

    private async Task RelayAgentActionAsync(HttpListenerContext ctx, RmsConnection conn, AgentInfo info, string action, CancellationToken ct)
    {
        if (action.Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            info = await conn.Client.GetAgentInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);
            await WriteJsonAsync(ctx.Response, new
            {
                machineName = info.MachineName,
                userName = info.LoggedInUser,
                osVersion = info.OsVersion,
                agentVersion = info.AgentVersion,
                screenWidth = info.PrimaryScreenWidth,
                screenHeight = info.PrimaryScreenHeight,
                monitorCount = info.MonitorCount,
            });
            return;
        }

        if (action.Equals("inventory", StringComparison.OrdinalIgnoreCase))
        {
            var inv = await conn.Client.GetInventoryAsync(new Empty(), cancellationToken: ct);
            await WriteJsonAsync(ctx.Response, new
            {
                cpu = inv.Hardware?.Cpu ?? "",
                gpu = inv.Hardware?.Gpu ?? "",
                ramGb = inv.Hardware is not null ? Math.Round(inv.Hardware.TotalRamBytes / 1073741824.0, 1) : 0,
                disks = inv.Hardware?.Disks.Select(d => new
                {
                    d.Name,
                    TotalGb = Math.Round(d.TotalBytes / 1073741824.0, 1),
                    FreeGb = Math.Round(d.FreeBytes / 1073741824.0, 1),
                }),
                appsCount = inv.Apps.Count,
                processesCount = inv.Processes.Count,
            });
            return;
        }

        if (action.Equals("screen.jpg", StringComparison.OrdinalIgnoreCase))
        {
            using var call = conn.Client.StreamScreen(new ScreenRequest
            {
                Fps = 1,
                Quality = 75,
                MaxWidth = 1280,
                SupportsTiles = false,
                MonitorIndex = 0,
            }, cancellationToken: ct);

            if (await call.ResponseStream.MoveNext(ct))
            {
                var frame = call.ResponseStream.Current;
                var bytes = frame.Jpeg.ToByteArray();
                ctx.Response.ContentType = "image/jpeg";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                ctx.Response.Close();
                return;
            }
        }

        if (action.Equals("shell", StringComparison.OrdinalIgnoreCase) && ctx.Request.HttpMethod == "POST")
        {
            var body = await ReadBodyJsonAsync(ctx.Request);
            var cmd = GetString(body, "command");
            using var shell = conn.Client.Shell(cancellationToken: ct);
            await shell.RequestStream.WriteAsync(new ShellInput { Data = cmd + "\n" }, ct);
            await shell.RequestStream.WriteAsync(new ShellInput { Close = true }, ct);
            await shell.RequestStream.CompleteAsync();
            var sb = new StringBuilder();
            while (await shell.ResponseStream.MoveNext(ct))
                sb.Append(shell.ResponseStream.Current.Data);
            await WriteJsonAsync(ctx.Response, new { output = sb.ToString() });
            return;
        }

        ctx.Response.StatusCode = 404;
        await WriteJsonAsync(ctx.Response, new { error = $"Unknown relay action '{action}'" });
    }

    private MachineProfile? FindWebEndpoint(string host) =>
        _getEndpoints().FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.WebUrl) &&
            (e.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
             (Uri.TryCreate(e.WebUrl, UriKind.Absolute, out var u) && u.Host.Equals(host, StringComparison.OrdinalIgnoreCase))));

    private void PruneSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, exp) in _mobileSessions)
            if (exp <= now) _mobileSessions.TryRemove(token, out _);
        // Bound memory: drop the sessions closest to expiry once the cap is reached.
        var excess = _mobileSessions.Count - MaxSessions + 1;
        if (excess > 0)
            foreach (var (token, _) in _mobileSessions.OrderBy(kv => kv.Value).Take(excess).ToList())
                _mobileSessions.TryRemove(token, out _);
    }

    private static string GetString(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private bool IsMobileAuthed(HttpListenerRequest req)
    {
        var cookie = req.Cookies["hrms_session"]?.Value;
        if (string.IsNullOrEmpty(cookie)) return false;
        if (_mobileSessions.TryGetValue(cookie, out var exp) && exp > DateTimeOffset.UtcNow)
            return true;
        _mobileSessions.TryRemove(cookie, out _);
        return false;
    }

    private static async Task<JsonElement> ReadBodyJsonAsync(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBodyBytes) throw new InvalidDataException("Request body too large.");
        var buffer = new char[MaxBodyBytes];
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        var raw = new string(buffer, 0, read);
        if (string.IsNullOrWhiteSpace(raw)) return default;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default; // malformed body: treated like an empty one
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        foreach (var job in _deployJobs.Values) job.Cts.Cancel();
        _ownPool?.Dispose();
        foreach (var c in _connectionPool.Values) c.Dispose();
        _connectionPool.Clear();
    }
}
