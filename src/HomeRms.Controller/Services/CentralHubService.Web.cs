using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Shared;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Controller.Services;

/// <summary>
/// Web Console JSON API (<c>/api/v2/...</c>) and static single-page app of the Central Hub.
/// <para>
/// Every route runs after the hub's sign-in gate and CSRF-header check (see <see cref="HandleRequestAsync"/>),
/// only reaches endpoints saved in the fleet (looked up by an opaque id, never by an arbitrary host),
/// pairs agents with the non-interactive GetAgentInfo handshake of the shared <see cref="AgentConnectionPool"/>,
/// and writes every state-changing call to the activity log with the browser session as operator.
/// Errors are always <c>{ ok: false, error, code }</c> with a matching HTTP status.
/// </para>
/// </summary>
public sealed partial class CentralHubService
{
    public const long MaxWebUploadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxShellOutputChars = 1_000_000;
    private const int MaxCommandChars = 64 * 1024;

    private readonly TransferLimiter _transfers = new();
    private readonly ConcurrentDictionary<string, WebDeployJob> _deployJobs = new();
    private AgentConnectionPool? _ownPool;

    /// <summary>Controller services for the web console (pool, telemetry, winget index); set once the UI is up.</summary>
    public HubConsoleServices? Console { get; set; }

    private AgentConnectionPool Pool => Console?.Pool ?? LazyInitializer.EnsureInitialized(ref _ownPool, () => new AgentConnectionPool());

    // ---------------- Static single-page app ----------------

    private static async Task<bool> TryServeAssetAsync(HttpListenerContext ctx, string path)
    {
        if (ctx.Request.HttpMethod is not ("GET" or "HEAD")) return false;
        if (path.Equals("/mobile", StringComparison.OrdinalIgnoreCase) || path.Equals("/mobile/", StringComparison.OrdinalIgnoreCase))
        {
            // The old mobile page is replaced by the web console.
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers["Location"] = "/";
            ctx.Response.Close();
            return true;
        }
        if (!WebAssets.Routes.TryGetValue(path, out var asset)) return false;
        var bytes = WebAssets.Get(asset.Resource);
        if (bytes is null) return false;

        var res = ctx.Response;
        res.Headers["Content-Security-Policy"] = WebAssets.ContentSecurityPolicy;
        res.Headers["X-Frame-Options"] = "DENY";
        res.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        res.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        res.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        res.Headers["Cache-Control"] = "no-store";
        res.ContentType = asset.ContentType;
        res.ContentLength64 = bytes.Length;
        if (ctx.Request.HttpMethod == "GET") await res.OutputStream.WriteAsync(bytes);
        res.Close();
        return true;
    }

    // ---------------- Sessions / audit ----------------

    /// <summary>"web:3f9a12c0@192.168.1.20": a short hash of the session cookie (never the token itself) and the peer IP.</summary>
    private string SessionOperator(HttpListenerRequest req)
    {
        var token = req.Cookies["hrms_session"]?.Value;
        var id = string.IsNullOrEmpty(token)
            ? "anonymous"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..8].ToLowerInvariant();
        return $"web:{id}@{req.RemoteEndPoint?.Address}";
    }

    private void Audit(HttpListenerContext ctx, string what) => _logEvent("WEB", $"[{SessionOperator(ctx.Request)}] {what}");

    // ---------------- Helpers ----------------

    private List<MachineProfile> SnapshotEndpoints()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return _getEndpoints().ToList(); }
            catch (InvalidOperationException) when (attempt < 3) { Thread.Sleep(10); } // collection changed on the UI thread
        }
    }

    private MachineProfile FindEndpoint(string id) =>
        SnapshotEndpoints().FirstOrDefault(m => WebAssets.EndpointId(m) == id)
        ?? throw new HubApiException(404, "unknown_endpoint", "No saved endpoint has this id.");

    private static void RequireAgent(MachineProfile m)
    {
        if (!m.IsAgentEndpoint)
            throw new HubApiException(400, "not_an_agent", $"'{m.DisplayTitle}' is not a Home RMS agent endpoint ({m.ModeBadge}).");
    }

    private static string OsFamily(MachineProfile m) => m.IsLinuxAgent || m.Kind == EndpointKind.Linux ? "linux" : m.IsWindows ? "windows" : "other";

    private static string[] PowerCaps(MachineProfile m)
    {
        var caps = new List<string>();
        if (PowerActions.NormalizeMac(m.MacAddress) is not null) caps.Add("wake");
        if (m.IsAgentEndpoint) caps.AddRange(["restart", "shutdown", "sleep", "lock", "logoff", "cancel"]);
        else if (m.IsAgentless) caps.AddRange(["restart", "shutdown", "logoff", "cancel"]);
        return caps.ToArray();
    }

    private object EndpointDto(MachineProfile m) => new
    {
        id = WebAssets.EndpointId(m),
        name = m.DisplayTitle,
        host = m.Host,
        port = m.Port,
        address = m.AddressLine,
        kind = m.Kind.ToString(),
        mode = m.ModeBadge,
        os = m.OsTag,
        osVersion = m.OsName,
        osFamily = OsFamily(m),
        isAgent = m.IsAgentEndpoint,
        isLinux = m.IsLinuxAgent,
        online = m.IsOnline || m.IsConnected,
        connected = m.IsConnected,
        status = m.StatusLabel,
        lastSeen = m.LastSeenUtc?.ToString("O"),
        health = m.HealthLine,
        cpu = Math.Round(m.CpuPercent, 1),
        ram = Math.Round(m.RamPercent, 1),
        latencyMs = m.LatencyMs,
        alerts = m.UnreadAlerts,
        caps = new
        {
            shell = m.IsAgentEndpoint || m.IsAgentless || m.IsAppliance,
            shellName = m.IsLinuxAgent || m.IsAppliance ? "bash" : "PowerShell",
            files = m.IsAgentEndpoint,
            services = m.IsAgentEndpoint,
            tasks = m.IsAgentEndpoint,
            inventory = m.IsAgentEndpoint,
            telemetry = m.IsAgentEndpoint,
            screen = m.IsAgentEndpoint,
            deploy = m.IsWindowsAgent,
            upgradeAll = m.IsWindowsAgent && Console?.UpgradeAll is not null,
            power = PowerCaps(m),
        },
    };

    private static async Task WriteJsonStatusAsync(HttpListenerResponse res, int status, object payload)
    {
        res.StatusCode = status;
        res.Headers["Cache-Control"] = "no-store";
        await WriteJsonAsync(res, payload);
    }

    private static Task WriteErrorAsync(HttpListenerResponse res, int status, string code, string message) =>
        WriteJsonStatusAsync(res, status, new { ok = false, error = message, code });

    private static string Query(HttpListenerRequest req, string name) => req.QueryString[name] ?? "";

    private static string Required(JsonElement body, string name)
    {
        var v = GetString(body, name);
        if (string.IsNullOrWhiteSpace(v)) throw new HubApiException(400, "missing_field", $"'{name}' is required.");
        return v;
    }

    private static bool GetBool(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    /// <summary>Loose sanity check for service / task names (the agent applies its strict allowlist regexes).</summary>
    private static string SafeTarget(string v)
    {
        v = v.Trim();
        if (v.Length is 0 or > 512 || v.Any(char.IsControl))
            throw new HubApiException(400, "invalid_target", "Invalid name.");
        return v;
    }

    private static (int Status, string Code, string Message) Classify(Exception ex) => ex switch
    {
        HubApiException h => (h.Status, h.Code, h.Message),
        AgentPairingException => (403, "pairing_required", ex.Message),
        RpcException { StatusCode: StatusCode.PermissionDenied } r => (403, "permission_denied", AgentErrors.Describe(r)),
        RpcException { StatusCode: StatusCode.Unauthenticated } r => (403, "pairing_required", AgentErrors.Describe(r)),
        RpcException { StatusCode: StatusCode.Unavailable } r => (502, "agent_unreachable", AgentErrors.Describe(r)),
        RpcException { StatusCode: StatusCode.DeadlineExceeded } r => (504, "timeout", AgentErrors.Describe(r)),
        RpcException { StatusCode: StatusCode.InvalidArgument } r => (400, "invalid_argument", r.Status.Detail),
        RpcException { StatusCode: StatusCode.NotFound } r => (404, "not_found", r.Status.Detail),
        RpcException { StatusCode: StatusCode.AlreadyExists } r => (409, "already_exists", r.Status.Detail),
        RpcException { StatusCode: StatusCode.FailedPrecondition } r => (409, "failed_precondition", r.Status.Detail),
        RpcException { StatusCode: StatusCode.ResourceExhausted } r => (429, "resource_exhausted", r.Status.Detail),
        RpcException { StatusCode: StatusCode.Unimplemented } r => (501, "not_supported", AgentErrors.Describe(r)),
        RpcException r => (502, "agent_error", AgentErrors.Describe(r)),
        InvalidDataException => (413, "too_large", ex.Message),
        OperationCanceledException => (504, "timeout", "The operation was cancelled or timed out."),
        _ => (500, "internal_error", ex.Message.Split('\n')[0]),
    };

    private async Task<T> AgentCallAsync<T>(MachineProfile m, Func<RemoteManagement.RemoteManagementClient, Task<T>> call, CancellationToken ct)
    {
        RequireAgent(m);
        var conn = await Pool.GetPairedAsync(m, interactive: false, ct);
        try
        {
            return await call(conn.Client);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            Pool.MarkUnpaired(m);
            throw;
        }
    }

    private Task<SystemOpResult> SystemOpAsync(MachineProfile m, string area, string action, string target, CancellationToken ct) =>
        AgentCallAsync(m, c => c.SystemOpAsync(new SystemOpRequest { Area = area, Action = action, Target = target, Extra = "" },
            deadline: DateTime.UtcNow.AddMinutes(3), cancellationToken: ct).ResponseAsync, ct);

    private static object TableDto(SystemOpResult res)
    {
        var table = SystemCatalog.ParseTable(res.JsonData);
        return new { ok = true, elevated = res.IsElevated, columns = table.Columns, rows = table.Rows, message = res.Message };
    }

    // ---------------- Router ----------------

    private async Task HandleV2Async(HttpListenerContext ctx, string path, string method, CancellationToken ct)
    {
        var res = ctx.Response;
        res.Headers["Cache-Control"] = "no-store";
        var seg = path["/api/v2/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (seg)
            {
                case ["overview"] when method == "GET":
                    await WriteJsonAsync(res, Overview(ctx));
                    return;
                case ["endpoints"] when method == "GET":
                    await WriteJsonAsync(res, new { ok = true, endpoints = SnapshotEndpoints().Select(EndpointDto).ToList() });
                    return;
                case ["endpoints", var id] when method == "GET":
                    await WriteJsonAsync(res, new { ok = true, endpoint = EndpointDto(FindEndpoint(id)) });
                    return;
                case ["endpoints", var id, .. var rest]:
                    await HandleEndpointAsync(ctx, FindEndpoint(id), rest, method, ct);
                    return;
                case ["alerts", "ack"] when method == "POST":
                {
                    var body = await ReadBodyJsonAsync(ctx.Request);
                    var alertId = GetString(body, "id");
                    if (string.IsNullOrWhiteSpace(alertId)) _alerts.AcknowledgeAll(); else _alerts.Acknowledge(alertId);
                    Audit(ctx, string.IsNullOrWhiteSpace(alertId) ? "acknowledged all alerts" : $"acknowledged alert {alertId}");
                    await WriteJsonAsync(res, new { ok = true, unread = _alerts.UnreadCount });
                    return;
                }
                case ["endpoints-scan"] when method == "POST":
                    Audit(ctx, "started a LAN scan / probe");
                    _ = _triggerLanScan();
                    await WriteJsonAsync(res, new { ok = true });
                    return;
                case ["winget", "search"] when method == "GET":
                    await WriteJsonAsync(res, WingetSearch(ctx.Request));
                    return;
                case ["deploy"] when method == "POST":
                    await WriteJsonAsync(res, await StartDeployAsync(ctx));
                    return;
                case ["deploy"] when method == "GET":
                    await WriteJsonAsync(res, new { ok = true, jobs = _deployJobs.Values.OrderByDescending(j => j.CreatedUtc).Take(10).Select(j => j.Snapshot()).ToList() });
                    return;
                case ["deploy", var jobId] when method == "GET":
                    if (!_deployJobs.TryGetValue(jobId, out var job)) throw new HubApiException(404, "unknown_job", "Unknown deployment.");
                    await WriteJsonAsync(res, job.Snapshot());
                    return;
                case ["deploy", var jobId, "cancel"] when method == "POST":
                    if (!_deployJobs.TryGetValue(jobId, out var cjob)) throw new HubApiException(404, "unknown_job", "Unknown deployment.");
                    cjob.Cts.Cancel();
                    Audit(ctx, $"cancelled deployment {jobId}");
                    await WriteJsonAsync(res, new { ok = true });
                    return;
                default:
                    throw new HubApiException(404, "not_found", "Unknown API route.");
            }
        }
        catch (Exception ex)
        {
            var (status, code, message) = Classify(ex);
            try { await WriteErrorAsync(res, status, code, message); }
            catch { try { res.Abort(); } catch { /* already gone */ } }
        }
    }

    private object Overview(HttpListenerContext ctx)
    {
        var eps = SnapshotEndpoints();
        var online = eps.Where(e => e.IsOnline || e.IsConnected).ToList();
        var reporting = online.Where(e => e.IsAgentEndpoint && (e.CpuPercent > 0 || e.RamPercent > 0)).ToList();
        var alerts = _alerts.Alerts.ToList();
        return new
        {
            ok = true,
            @operator = SessionOperator(ctx.Request),
            hub = new { port = Port, lanBound = LanBound, authRequired = AuthRequired, relayedRequests = RelayedRequests, machine = Environment.MachineName },
            endpoints = new
            {
                total = eps.Count,
                online = online.Count,
                offline = eps.Count - online.Count,
                agents = eps.Count(e => e.IsAgentEndpoint),
                linux = eps.Count(e => e.IsLinuxAgent),
            },
            fleet = new
            {
                reporting = reporting.Count,
                cpu = reporting.Count == 0 ? 0 : Math.Round(reporting.Average(e => e.CpuPercent), 1),
                ram = reporting.Count == 0 ? 0 : Math.Round(reporting.Average(e => e.RamPercent), 1),
                hottest = reporting.OrderByDescending(e => Math.Max(e.CpuPercent, e.RamPercent)).Take(5)
                    .Select(e => new { id = WebAssets.EndpointId(e), name = e.DisplayTitle, cpu = Math.Round(e.CpuPercent, 1), ram = Math.Round(e.RamPercent, 1) }).ToList(),
            },
            alerts = new
            {
                unread = alerts.Count(a => !a.Acknowledged),
                items = alerts.Take(12).Select(a => new
                {
                    id = a.Id, endpoint = a.EndpointName, severity = a.SeverityText, title = a.Title, detail = a.Detail,
                    time = a.Timestamp.ToString("O"), acknowledged = a.Acknowledged,
                }).ToList(),
            },
            features = new
            {
                deploy = Console?.WingetIndex is not null,
                upgradeAll = Console?.UpgradeAll is not null,
            },
        };
    }

    // ---------------- Per-endpoint routes ----------------

    private async Task HandleEndpointAsync(HttpListenerContext ctx, MachineProfile m, string[] rest, string method, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var linux = m.IsLinuxAgent;
        switch (rest)
        {
            case ["info"] when method == "GET":
            {
                var info = await AgentCallAsync(m, c => c.GetAgentInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(15), cancellationToken: ct).ResponseAsync, ct);
                await WriteJsonAsync(res, new
                {
                    ok = true, machineName = info.MachineName, user = info.LoggedInUser, osVersion = info.OsVersion, agentVersion = info.AgentVersion,
                    screen = $"{info.PrimaryScreenWidth}×{info.PrimaryScreenHeight}", monitors = info.MonitorCount,
                });
                return;
            }

            case ["power"] when method == "POST":
            {
                var body = await ReadBodyJsonAsync(req);
                var action = PowerActions.Find(Required(body, "action"))
                             ?? throw new HubApiException(400, "invalid_action", "Unknown power action.");
                if (!PowerCaps(m).Contains(action.Id))
                    throw new HubApiException(400, "not_supported", $"'{action.Label}' is not available for {m.DisplayTitle}.");
                Audit(ctx, $"power/{action.Id} on {m.DisplayTitle}");
                var msg = await PowerActions.ExecuteAsync(action, m, Pool, _agentless, ct, interactive: false);
                await WriteJsonAsync(res, new { ok = true, message = msg });
                return;
            }

            case ["shell"] when method == "POST":
                await RunShellAsync(ctx, m, ct);
                return;

            // ---- files ----
            case ["files", "drives"] when method == "GET":
            {
                var drives = await AgentCallAsync(m, c => c.ListDrivesAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: ct).ResponseAsync, ct);
                var list = drives.Drives.Select(d => new { name = d.Name, label = d.Label, type = d.DriveType, format = d.Format, total = d.TotalBytes, free = d.FreeBytes, ready = d.IsReady }).ToList();
                if (linux && list.Count == 0) list.Add(new { name = "/", label = "/", type = "Fixed", format = "", total = 0L, free = 0L, ready = true });
                await WriteJsonAsync(res, new { ok = true, linux, drives = list });
                return;
            }
            case ["files", "list"] when method == "GET":
            {
                var p = WebPaths.ValidatePath(Query(req, "path"), linux);
                var listing = await AgentCallAsync(m, c => c.ListDirectoryAsync(new ListDirectoryRequest { Path = p, MaxEntries = 5000 },
                    deadline: DateTime.UtcNow.AddSeconds(60), cancellationToken: ct).ResponseAsync, ct);
                await WriteJsonAsync(res, new
                {
                    ok = true, path = listing.Path, parent = listing.ParentPath, truncated = listing.Truncated, linux,
                    entries = listing.Entries.Select(e => new { name = e.Name, path = e.FullPath, dir = e.IsDir, link = e.IsLink, size = e.Size, modified = e.ModifiedUnixMs, attributes = e.Attributes }).ToList(),
                });
                return;
            }
            case ["files", "download"] when method == "GET":
                await DownloadAsync(ctx, m, WebPaths.ValidatePath(Query(req, "path"), linux), ct);
                return;
            case ["files", "upload"] when method == "POST":
                await UploadAsync(ctx, m, ct);
                return;
            case ["files", "delete"] when method == "POST":
            {
                var body = await ReadBodyJsonAsync(req);
                var p = WebPaths.ValidatePath(Required(body, "path"), linux);
                var recursive = GetBool(body, "recursive");
                Audit(ctx, $"delete '{p}'{(recursive ? " (recursive)" : "")} on {m.DisplayTitle}");
                var r = await AgentCallAsync(m, c => c.DeletePathAsync(new DeletePathRequest { Path = p, Recursive = recursive },
                    deadline: DateTime.UtcNow.AddMinutes(5), cancellationToken: ct).ResponseAsync, ct);
                await WriteFileOpAsync(res, r);
                return;
            }
            case ["files", "rename"] when method == "POST":
            {
                var body = await ReadBodyJsonAsync(req);
                var p = WebPaths.ValidatePath(Required(body, "path"), linux);
                var name = WebPaths.ValidateName(Required(body, "newName"), linux);
                var dest = WebPaths.Combine(WebPaths.ParentOf(p, linux), name, linux);
                Audit(ctx, $"rename '{p}' -> '{dest}' on {m.DisplayTitle}");
                var r = await AgentCallAsync(m, c => c.RenamePathAsync(new RenamePathRequest { SourcePath = p, DestinationPath = dest },
                    deadline: DateTime.UtcNow.AddSeconds(60), cancellationToken: ct).ResponseAsync, ct);
                await WriteFileOpAsync(res, r);
                return;
            }
            case ["files", "mkdir"] when method == "POST":
            {
                var body = await ReadBodyJsonAsync(req);
                var dir = WebPaths.ValidatePath(Required(body, "dir"), linux);
                var full = WebPaths.Combine(dir, WebPaths.ValidateName(Required(body, "name"), linux), linux);
                Audit(ctx, $"create folder '{full}' on {m.DisplayTitle}");
                var r = await AgentCallAsync(m, c => c.CreateDirectoryAsync(new FilePathRequest { Path = full },
                    deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: ct).ResponseAsync, ct);
                await WriteFileOpAsync(res, r);
                return;
            }

            // ---- services / tasks ----
            case ["services"] when method == "GET":
            {
                var r = await SystemOpAsync(m, "services", "list", "", ct);
                if (!r.Success) throw new HubApiException(502, "agent_failed", r.Message);
                await WriteJsonAsync(res, TableDto(r));
                return;
            }
            case ["services", var action] when method == "POST" && action is "start" or "stop" or "restart":
            case ["tasks", var taskAction] when method == "POST" && taskAction is "run" or "enable" or "disable":
            {
                var area = rest[0];
                var op = rest[1];
                var body = await ReadBodyJsonAsync(req);
                var name = SafeTarget(Required(body, "name"));
                Audit(ctx, $"{area}/{op} '{name}' on {m.DisplayTitle}");
                var r = await SystemOpAsync(m, area, op, name, ct);
                await WriteJsonStatusAsync(res, r.Success ? 200 : 422, r.Success
                    ? new { ok = true, message = r.Message }
                    : new { ok = false, error = r.Message, code = "agent_failed" } as object);
                return;
            }
            case ["tasks"] when method == "GET":
            {
                var r = await SystemOpAsync(m, "tasks", "list", "", ct);
                if (!r.Success) throw new HubApiException(502, "agent_failed", r.Message);
                await WriteJsonAsync(res, TableDto(r));
                return;
            }

            // ---- inventory / processes ----
            case ["inventory"] when method == "GET":
            {
                var inv = await AgentCallAsync(m, c => c.GetInventoryAsync(new Empty(), deadline: DateTime.UtcNow.AddMinutes(2), cancellationToken: ct).ResponseAsync, ct);
                await WriteJsonAsync(res, new
                {
                    ok = true,
                    collected = inv.CollectedAtUtc,
                    hardware = new
                    {
                        cpu = inv.Hardware?.Cpu ?? "", cores = inv.Hardware?.LogicalCores ?? 0, ramBytes = inv.Hardware?.TotalRamBytes ?? 0, gpu = inv.Hardware?.Gpu ?? "",
                        disks = inv.Hardware?.Disks.Select(d => new { name = d.Name, total = d.TotalBytes, free = d.FreeBytes }).ToList(),
                    },
                    processes = inv.Processes.OrderByDescending(p => p.WorkingSetBytes).Take(2000)
                        .Select(p => new { pid = p.Pid, name = p.Name, memory = p.WorkingSetBytes }).ToList(),
                    apps = inv.Apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Take(5000)
                        .Select(a => new { name = a.Name, version = a.Version, publisher = a.Publisher, installed = a.InstallDate }).ToList(),
                });
                return;
            }
            case ["processes", "kill"] when method == "POST":
            {
                var body = await ReadBodyJsonAsync(req);
                var pid = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("pid", out var pidEl) && pidEl.TryGetInt32(out var v) ? v : 0;
                if (pid <= 0) throw new HubApiException(400, "invalid_pid", "A positive process id is required.");
                Audit(ctx, $"kill process {pid} on {m.DisplayTitle}");
                var r = await AgentCallAsync(m, c => c.KillProcessAsync(new KillProcessRequest { Pid = pid }, deadline: DateTime.UtcNow.AddSeconds(20), cancellationToken: ct).ResponseAsync, ct);
                await WriteJsonStatusAsync(res, r.Success ? 200 : 422, r.Success
                    ? new { ok = true, message = r.Message }
                    : new { ok = false, error = r.Message, code = "agent_failed" } as object);
                return;
            }

            // ---- telemetry / screen ----
            case ["telemetry"] when method == "GET":
            {
                RequireAgent(m);
                var t = Console?.Telemetry?.Invoke(m);
                await WriteJsonAsync(res, t is null
                    ? new { ok = true, live = false, status = "Not streaming (the Controller streams telemetry from online, paired agents)." } as object
                    : new { ok = true, live = t.Live, status = t.Status, cpu = t.Cpu, ram = t.Ram, disk = t.Disk, net = t.Net, uptime = t.UptimeSeconds, processes = t.ProcessCount, rx = t.RxBps, tx = t.TxBps });
                return;
            }
            case ["screen.jpg"] when method == "GET":
                await ScreenSnapshotAsync(ctx, m, ct);
                return;

            case ["upgrade-all"] when method == "POST":
            {
                // TODO(UpgradeAll): replace with the UpgradeAll RPC client after the Update All work merges
                // (see HubConsoleServices.UpgradeAll). Until then the hook is null and the route answers 501.
                var hook = Console?.UpgradeAll ?? throw new HubApiException(501, "not_implemented", "Update All is not available in this Controller version yet.");
                if (!m.IsWindowsAgent) throw new HubApiException(400, "not_supported", "Update All uses winget and is available on Windows agents only.");
                Audit(ctx, $"Update All (winget upgrade --all) on {m.DisplayTitle}");
                var lines = new List<string>();
                var (ok, message) = await hook(m, l => { lock (lines) if (lines.Count < 500) lines.Add(l); }, ct);
                await WriteJsonStatusAsync(res, ok ? 200 : 422, new { ok, message, log = lines });
                return;
            }

            default:
                throw new HubApiException(404, "not_found", "Unknown endpoint route.");
        }
    }

    private static async Task WriteFileOpAsync(HttpListenerResponse res, FileOpResult r) =>
        await WriteJsonStatusAsync(res, r.Success ? 200 : 422, r.Success
            ? new { ok = true, message = r.Message, path = r.Path }
            : new { ok = false, error = r.Message, code = "agent_failed" } as object);

    // ---------------- Shell ----------------

    private async Task RunShellAsync(HttpListenerContext ctx, MachineProfile m, CancellationToken ct)
    {
        var body = await ReadBodyJsonAsync(ctx.Request);
        var command = Required(body, "command");
        if (command.Length > MaxCommandChars) throw new HubApiException(413, "too_large", "The command is too long (64 KB max).");
        var timeoutSec = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("timeoutSeconds", out var tEl) && tEl.TryGetInt32(out var tv) ? Math.Clamp(tv, 5, 1800) : 300;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        var firstLine = command.Split('\n')[0].Trim();
        Audit(ctx, $"shell on {m.DisplayTitle}: {(firstLine.Length > 120 ? firstLine[..120] + "…" : firstLine)}{(command.Contains('\n') ? " (multi-line)" : "")}");

        if (m.IsAgentless)
        {
            var (ok, output) = await _agentless.RunShellAsync(m, command, cts.Token);
            await WriteJsonAsync(ctx.Response, new { ok, output, shell = "PowerShell (WinRM)", exitCode = (int?)null });
            return;
        }
        if (!m.IsAgentEndpoint && m.IsAppliance)
        {
            var (ok, output) = await _ssh.ExecuteSshAsync(m, command, cts.Token);
            await WriteJsonAsync(ctx.Response, new { ok, output, shell = "ssh", exitCode = (int?)null });
            return;
        }
        RequireAgent(m);

        var linux = m.IsLinuxAgent;
        var wrapped = linux ? ScriptExecutor.BuildBash(command, "Web console") : ScriptExecutor.BuildPowerShell(ScriptShell.PowerShell, command, "Web console");
        var conn = await Pool.GetPairedAsync(m, interactive: false, cts.Token);
        var sb = new StringBuilder();
        var truncated = false;
        try
        {
            using var call = conn.Client.Shell(cancellationToken: cts.Token);
            await call.RequestStream.WriteAsync(new ShellInput { Data = wrapped }, cts.Token);
            await call.RequestStream.WriteAsync(new ShellInput { Close = true }, cts.Token);
            await call.RequestStream.CompleteAsync();
            await foreach (var o in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                if (sb.Length < MaxShellOutputChars) sb.Append(o.Data);
                else truncated = true;
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            Pool.MarkUnpaired(m);
            throw;
        }

        var all = sb.ToString();
        var idx = all.LastIndexOf(ScriptExecutor.DoneMarker, StringComparison.Ordinal);
        int? exit = null;
        var hadError = false;
        if (idx >= 0)
        {
            var parts = all[(idx + ScriptExecutor.DoneMarker.Length)..].Split(['|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            hadError = parts.Length > 0 && parts[0].Trim() == "1";
            exit = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var code) ? code : 0;
            all = all[..idx];
        }
        await WriteJsonAsync(ctx.Response, new
        {
            ok = idx >= 0 && !hadError && exit == 0,
            output = all,
            exitCode = exit,
            completed = idx >= 0,
            truncated,
            shell = linux ? "bash" : "PowerShell",
        });
    }

    // ---------------- File transfer ----------------

    private IDisposable AcquireTransfer(HttpListenerContext ctx)
    {
        var lease = _transfers.TryAcquire(SessionOperator(ctx.Request), out var reason);
        if (lease is null)
        {
            ctx.Response.Headers["Retry-After"] = "5";
            throw new HubApiException(429, "rate_limited", reason);
        }
        return lease;
    }

    private async Task DownloadAsync(HttpListenerContext ctx, MachineProfile m, string path, CancellationToken ct)
    {
        RequireAgent(m);
        using var lease = AcquireTransfer(ctx);
        var conn = await Pool.GetPairedAsync(m, interactive: false, ct);
        Audit(ctx, $"download '{path}' from {m.DisplayTitle}");

        using var call = conn.Client.DownloadFile(new FilePathRequest { Path = path }, cancellationToken: ct);
        var res = ctx.Response;
        var started = false;
        try
        {
            await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct))
            {
                if (!started)
                {
                    var name = string.IsNullOrEmpty(chunk.FileName) ? Path.GetFileName(path.Replace('\\', '/')) : chunk.FileName;
                    res.StatusCode = 200;
                    res.ContentType = "application/octet-stream";
                    res.Headers["Content-Disposition"] = WebPaths.ContentDisposition(name);
                    res.Headers["Cache-Control"] = "no-store";
                    if (chunk.TotalSize > 0) res.ContentLength64 = chunk.TotalSize;
                    else res.SendChunked = true;
                    started = true;
                }
                await res.OutputStream.WriteAsync(chunk.Data.Memory, ct);
            }
            if (!started)
            {
                // Empty file: the agent may send no data chunk at all.
                res.ContentType = "application/octet-stream";
                res.Headers["Content-Disposition"] = WebPaths.ContentDisposition(Path.GetFileName(path.Replace('\\', '/')));
                res.ContentLength64 = 0;
            }
            res.Close();
        }
        catch (Exception) when (started)
        {
            try { res.Abort(); } catch { /* client gone */ }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            Pool.MarkUnpaired(m);
            throw;
        }
    }

    private async Task UploadAsync(HttpListenerContext ctx, MachineProfile m, CancellationToken ct)
    {
        RequireAgent(m);
        var req = ctx.Request;
        var linux = m.IsLinuxAgent;
        var dir = WebPaths.ValidatePath(Query(req, "dir"), linux);
        var name = WebPaths.ValidateName(Query(req, "name"), linux);
        var overwrite = Query(req, "overwrite") is "1" or "true";
        var dest = WebPaths.Combine(dir, name, linux);
        if (req.ContentLength64 > MaxWebUploadBytes)
            throw new HubApiException(413, "too_large", $"Uploads are limited to {MaxWebUploadBytes / (1024 * 1024 * 1024)} GB.");

        using var lease = AcquireTransfer(ctx);
        var conn = await Pool.GetPairedAsync(m, interactive: false, ct);
        Audit(ctx, $"upload '{dest}' ({(req.ContentLength64 >= 0 ? FileRow.FormatSize(req.ContentLength64) : "streamed")}) to {m.DisplayTitle}");

        try
        {
            using var call = conn.Client.UploadFile(cancellationToken: ct);
            var buffer = new byte[FileManagerOps.ChunkSize];
            long total = 0;
            var first = true;
            while (true)
            {
                var n = await ReadFullAsync(req.InputStream, buffer, ct);
                total += n;
                if (total > MaxWebUploadBytes)
                    throw new HubApiException(413, "too_large", "Upload exceeds the 2 GB limit.");
                var chunk = new FileUploadChunk { Data = ByteString.CopyFrom(buffer, 0, n) };
                if (first)
                {
                    chunk.DestinationPath = dest;
                    chunk.Overwrite = overwrite;
                    if (req.ContentLength64 > 0) chunk.TotalSize = req.ContentLength64;
                    first = false;
                }
                await call.RequestStream.WriteAsync(chunk, ct);
                if (n < buffer.Length) break;
            }
            await call.RequestStream.CompleteAsync();
            var r = await call.ResponseAsync;
            await WriteFileOpAsync(ctx.Response, r);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            Pool.MarkUnpaired(m);
            throw;
        }
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    // ---------------- Screen snapshot ----------------

    private async Task ScreenSnapshotAsync(HttpListenerContext ctx, MachineProfile m, CancellationToken ct)
    {
        RequireAgent(m);
        var conn = await Pool.GetPairedAsync(m, interactive: false, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45)); // the first frame may wait for on-device consent
        using var call = conn.Client.StreamScreen(new ScreenRequest
        {
            Fps = 1, Quality = 70, MaxWidth = 1280, SupportsTiles = false, MonitorIndex = 0,
            OperatorName = "Home RMS web console",
        }, cancellationToken: cts.Token);
        if (!await call.ResponseStream.MoveNext(cts.Token))
            throw new HubApiException(502, "no_frame", "The agent did not send a screen frame.");
        var bytes = call.ResponseStream.Current.Jpeg.ToByteArray();
        var res = ctx.Response;
        res.ContentType = "image/jpeg";
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct);
        res.Close();
    }

    // ---------------- Deploy (winget) ----------------

    private object WingetSearch(HttpListenerRequest req)
    {
        var idx = Console?.WingetIndex;
        if (idx is null)
            return new { ok = true, available = false, message = "The winget catalog is not available in this hub.", total = 0, items = Array.Empty<object>() };
        if (!idx.IsAvailable)
        {
            if (Console!.AllowWingetIndexRefresh) _ = Task.Run(async () => { try { await idx.EnsureFreshAsync(false); } catch { /* status shows the error */ } });
            return new { ok = true, available = false, message = idx.Status.Error ?? "The winget index is loading; try again in a moment.", total = 0, items = Array.Empty<object>() };
        }
        var q = Query(req, "q");
        if (q.Length > 100) q = q[..100];
        var offset = int.TryParse(Query(req, "offset"), out var o) ? Math.Clamp(o, 0, 100_000) : 0;
        var limit = int.TryParse(Query(req, "limit"), out var l) ? Math.Clamp(l, 1, 100) : 40;
        var (items, total) = idx.Search(q, null, offset, limit);
        return new
        {
            ok = true, available = true, total,
            items = items.Select(p => new { id = p.Id, name = p.Name, version = p.Version, publisher = p.NormPublisher, tags = p.Tags.Take(6) }).ToList(),
        };
    }

    private async Task<object> StartDeployAsync(HttpListenerContext ctx)
    {
        var body = await ReadBodyJsonAsync(ctx.Request);
        List<string> Strings(string name) =>
            body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];

        var packages = Strings("packages");
        var endpointIds = Strings("endpoints");
        if (packages.Count == 0) throw new HubApiException(400, "missing_field", "Select at least one app.");
        if (packages.Count > WingetRules.MaxPackagesPerRequest) throw new HubApiException(400, "too_many", $"At most {WingetRules.MaxPackagesPerRequest} apps per deployment.");
        if (packages.FirstOrDefault(p => !WingetRules.IsValidPackageId(p)) is { } bad) throw new HubApiException(400, "invalid_package", $"Invalid winget package id '{bad}'.");
        if (endpointIds.Count == 0) throw new HubApiException(400, "missing_field", "Select at least one endpoint.");
        if (endpointIds.Count > 50) throw new HubApiException(400, "too_many", "At most 50 endpoints per deployment.");

        var targets = new List<WebDeployTarget>();
        foreach (var id in endpointIds)
        {
            var m = FindEndpoint(id);
            if (m.IsLinuxAgent || OsFamily(m) == "linux") throw new HubApiException(400, "windows_only", $"'{m.DisplayTitle}' is a Linux endpoint: winget deployment is Windows only.");
            if (!m.IsWindowsAgent) throw new HubApiException(400, "not_supported", $"'{m.DisplayTitle}' is not a Windows agent endpoint (use the Controller's Deploy view for WinRM endpoints).");
            var t = new WebDeployTarget { EndpointId = id, Name = m.DisplayTitle, Profile = m };
            foreach (var p in packages) t.Packages[p] = new WebDeployPackage { Id = p };
            targets.Add(t);
        }

        // Bound memory: keep the 20 most recent jobs.
        foreach (var old in _deployJobs.Values.Where(j => j.Done).OrderByDescending(j => j.CreatedUtc).Skip(19).ToList())
            _deployJobs.TryRemove(old.Id, out _);

        var job = new WebDeployJob { Id = Guid.NewGuid().ToString("N")[..12], Operator = SessionOperator(ctx.Request), Targets = targets };
        _deployJobs[job.Id] = job;
        var silent = !body.TryGetProperty("silent", out var sEl) || sEl.ValueKind != JsonValueKind.False;
        var machineScope = GetBool(body, "machineScope");
        var upgrade = !body.TryGetProperty("upgrade", out var uEl) || uEl.ValueKind != JsonValueKind.False;
        Audit(ctx, $"winget deploy {job.Id}: {string.Join(", ", packages)} -> {string.Join(", ", targets.Select(t => t.Name))}");
        _ = Task.Run(() => RunDeployJobAsync(job, packages, silent, machineScope, upgrade));
        return job.Snapshot();
    }

    private async Task RunDeployJobAsync(WebDeployJob job, List<string> packages, bool silent, bool machineScope, bool upgrade)
    {
        var ct = job.Cts.Token;
        try
        {
            await Task.WhenAll(job.Targets.Select(async t =>
            {
                void Set(Action a) { lock (job.Gate) a(); }
                try
                {
                    Set(() => t.Status = "Pairing");
                    var conn = await Pool.GetPairedAsync(t.Profile, interactive: false, ct);
                    Set(() => t.Status = "Running");
                    var request = new WingetRequest { Silent = silent, MachineScope = machineScope, UpgradeIfInstalled = upgrade };
                    request.PackageIds.AddRange(packages);
                    using var call = conn.Client.RunWinget(request, cancellationToken: ct);
                    await foreach (var p in call.ResponseStream.ReadAllAsync(ct))
                    {
                        Set(() =>
                        {
                            if (!string.IsNullOrEmpty(p.LogLine)) t.AddLog((string.IsNullOrEmpty(p.PackageId) ? "" : p.PackageId + ": ") + p.LogLine);
                            if (!string.IsNullOrEmpty(p.PackageId) && t.Packages.TryGetValue(p.PackageId, out var pkg))
                            {
                                pkg.Phase = p.Phase.ToString();
                                if (p.Percent >= 0) pkg.Percent = p.Percent;
                                if (p.Phase is WingetPhase.Succeeded or WingetPhase.Failed or WingetPhase.Skipped)
                                {
                                    pkg.Percent = 100;
                                    pkg.Detail = string.IsNullOrWhiteSpace(p.ExitMessage) ? $"exit {p.ExitCode}" : p.ExitMessage;
                                }
                            }
                        });
                        if (p.Done) break;
                    }
                    Set(() => t.Status = t.Packages.Values.All(x => x.Phase is "Succeeded" or "Skipped") ? "Succeeded"
                        : t.Packages.Values.Any(x => x.Phase == "Failed") ? "Completed with errors" : "Finished");
                }
                catch (Exception ex)
                {
                    var (_, _, message) = Classify(ex);
                    if (ex is RpcException { StatusCode: StatusCode.PermissionDenied }) Pool.MarkUnpaired(t.Profile);
                    Set(() =>
                    {
                        t.Status = ct.IsCancellationRequested ? "Cancelled" : "Failed";
                        t.Error = ct.IsCancellationRequested ? "Cancelled." : message;
                        foreach (var pkg in t.Packages.Values.Where(x => x.Phase is not ("Succeeded" or "Failed" or "Skipped")))
                            pkg.Phase = ct.IsCancellationRequested ? "Cancelled" : "Failed";
                    });
                }
            }));
        }
        finally
        {
            lock (job.Gate) job.Done = true;
            var ok = job.Targets.Count(t => t.Status == "Succeeded");
            _logEvent("WEB", $"[{job.Operator}] winget deploy {job.Id} finished: {ok}/{job.Targets.Count} endpoint(s) succeeded.");
        }
    }
}
