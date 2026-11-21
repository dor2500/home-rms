using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HomeRms.Controller.Models;

namespace HomeRms.Controller.Services;

/// <summary>
/// What the browser console (Central Hub /api/v2) needs from the running Controller. Everything is
/// optional so the hub also runs headless (tests, scratch harnesses) with its own connection pool.
/// </summary>
public sealed class HubConsoleServices
{
    /// <summary>The Controller's pairing-aware pool (GetAgentInfo first; follows changed IPs via EndpointLocator).</summary>
    public AgentConnectionPool? Pool { get; init; }

    /// <summary>Microsoft's winget source index (Deploy search).</summary>
    public WingetSourceIndex? WingetIndex { get; init; }

    /// <summary>When true the hub may download / refresh the winget index on demand.</summary>
    public bool AllowWingetIndexRefresh { get; init; }

    /// <summary>Thread-safe snapshot of the live telemetry of one endpoint (null = not streaming).</summary>
    public Func<MachineProfile, WebTelemetry?>? Telemetry { get; init; }

    /// <summary>
    /// TODO(UpgradeAll): hook for "Update All" (winget upgrade --all) once the UpgradeAll RPC / client from the
    /// Update All work is merged. Wire it in MainWindow (InitializeFeatureViews) as
    /// <c>(m, log, ct) =&gt; ...call the UpgradeAll client, report each line through log...</c>.
    /// While it is null the web console hides the button and /api/v2/endpoints/{id}/upgrade-all answers 501.
    /// </summary>
    public Func<MachineProfile, Action<string>, CancellationToken, Task<(bool Ok, string Message)>>? UpgradeAll { get; init; }
}

/// <summary>Telemetry snapshot sent to the browser (last 5 minutes, oldest first).</summary>
public sealed record WebTelemetry(
    bool Live, string Status, double[] Cpu, double[] Ram, double[] Disk, double[] Net,
    long UptimeSeconds, int ProcessCount, double RxBps, double TxBps)
{
    /// <summary>Copies an <see cref="EndpointTelemetry"/>; call on the UI thread (it owns the buffers).</summary>
    public static WebTelemetry From(EndpointTelemetry et)
    {
        static double[] Tail(double[] s) => s.Length > 150 ? s[^150..] : s.ToArray();
        var last = et.Latest;
        return new WebTelemetry(et.IsLive, et.Status, Tail(et.CpuSeries), Tail(et.RamSeries), Tail(et.DiskSeries), Tail(et.NetSeries),
            et.UptimeSeconds, et.ProcessCount, last?.RxBps ?? 0, last?.TxBps ?? 0);
    }
}

/// <summary>An API error with an HTTP status and a stable machine-readable code.</summary>
public sealed class HubApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Hub-side validation of file-manager paths coming from the browser. The agent validates again
/// (SafePath); this layer rejects traversal and odd path forms before anything is relayed.
/// </summary>
public static class WebPaths
{
    public const int MaxPathLength = 4096;

    private static HubApiException Bad(string message) => new(400, "invalid_path", message);

    /// <summary>Validates an absolute remote path for a Linux ('/...') or Windows ('C:\...') agent.</summary>
    public static string ValidatePath(string? raw, bool linux)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw Bad("A path is required.");
        if (raw.Length > MaxPathLength) throw Bad("The path is too long.");
        if (raw.Any(char.IsControl)) throw Bad("The path contains control characters.");
        var segments = raw.Split('/', '\\');
        if (segments.Any(s => s == ".." || s == "."))
            throw Bad("Relative segments ('.' / '..') are not allowed.");

        if (linux)
        {
            if (!raw.StartsWith('/')) throw Bad("Linux paths must be absolute (start with '/').");
            if (raw.Contains("//", StringComparison.Ordinal) && raw != "/") throw Bad("Empty path segments are not allowed.");
            return raw.Length > 1 ? raw.TrimEnd('/') : raw;
        }

        var p = raw.Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) throw Bad("UNC and device paths are not allowed.");
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\')
            throw Bad(@"Windows paths must be absolute (for example C:\Users).");
        if (p.IndexOf(':', 2) >= 0) throw Bad("Alternate data streams are not allowed.");
        if (p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) throw Bad("The path contains characters that are not valid on Windows.");
        if (p[3..].Contains(@"\\", StringComparison.Ordinal)) throw Bad("Empty path segments are not allowed.");
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    /// <summary>Validates a single file / folder name (no separators, no traversal).</summary>
    public static string ValidateName(string? raw, bool linux)
    {
        var name = (raw ?? "").Trim();
        if (name.Length == 0) throw Bad("A name is required.");
        if (name.Length > 255) throw Bad("The name is too long.");
        if (name is "." or "..") throw Bad("'.' and '..' are not valid names.");
        if (name.Any(char.IsControl) || name.IndexOfAny(['/', '\\']) >= 0) throw Bad("Names cannot contain '/' or '\\'.");
        if (!linux && name.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0) throw Bad("The name contains characters that are not valid on Windows.");
        return name;
    }

    public static string Combine(string dir, string name, bool linux)
    {
        var sep = linux ? '/' : '\\';
        return dir.EndsWith(sep) ? dir + name : dir + sep + name;
    }

    public static string ParentOf(string path, bool linux)
    {
        var sep = linux ? '/' : '\\';
        var i = path.TrimEnd(sep).LastIndexOf(sep);
        if (i < 0) return "";
        return linux ? (i == 0 ? "/" : path[..i]) : (i <= 2 ? path[..3] : path[..i]);
    }

    /// <summary>Content-Disposition value with an ASCII fallback and an RFC 5987 UTF-8 name.</summary>
    public static string ContentDisposition(string fileName)
    {
        var ascii = new string(fileName.Select(c => c is >= ' ' and < (char)127 && c is not ('"' or '\\' or ';' or ',') ? c : '_').ToArray());
        if (ascii.Trim().Length == 0) ascii = "download";
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}

/// <summary>
/// File-transfer rate limiting for the browser console: a few concurrent transfers per session and in
/// total, and a cap on how many transfers one session may start per minute.
/// </summary>
public sealed class TransferLimiter(int perSession = 2, int total = 4, int startsPerMinute = 30)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _active = new();
    private readonly Dictionary<string, Queue<DateTime>> _starts = new();
    private int _total;

    /// <summary>Returns a lease to dispose when the transfer ends, or null with a reason when over a limit.</summary>
    public IDisposable? TryAcquire(string session, out string reason)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!_starts.TryGetValue(session, out var q)) _starts[session] = q = new Queue<DateTime>();
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();

            if (q.Count >= startsPerMinute) { reason = $"Too many transfers started (limit {startsPerMinute} per minute)."; return null; }
            if (_active.GetValueOrDefault(session) >= perSession) { reason = $"At most {perSession} transfers can run at once per browser session."; return null; }
            if (_total >= total) { reason = "The hub is busy with other transfers; try again shortly."; return null; }

            q.Enqueue(now);
            _active[session] = _active.GetValueOrDefault(session) + 1;
            _total++;
            reason = "";
            return new Lease(this, session);
        }
    }

    private void Release(string session)
    {
        lock (_gate)
        {
            _total = Math.Max(0, _total - 1);
            var n = _active.GetValueOrDefault(session) - 1;
            if (n <= 0) _active.Remove(session); else _active[session] = n;
        }
    }

    private sealed class Lease(TransferLimiter owner, string session) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(session);
        }
    }
}

/// <summary>A winget deployment started from the browser (progress is polled).</summary>
public sealed class WebDeployJob
{
    public required string Id { get; init; }
    public required string Operator { get; init; }
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public required List<WebDeployTarget> Targets { get; init; }
    public bool Done { get; set; }
    public readonly object Gate = new();
    public CancellationTokenSource Cts { get; } = new();

    public object Snapshot()
    {
        lock (Gate)
        {
            return new
            {
                ok = true,
                id = Id,
                created = CreatedUtc.ToString("O"),
                done = Done,
                targets = Targets.Select(t => new
                {
                    endpointId = t.EndpointId,
                    name = t.Name,
                    status = t.Status,
                    error = t.Error,
                    packages = t.Packages.Values.Select(p => new { id = p.Id, phase = p.Phase, percent = p.Percent, detail = p.Detail }).ToList(),
                    log = t.Log.TakeLast(60).ToList(),
                }).ToList(),
            };
        }
    }
}

public sealed class WebDeployTarget
{
    public required string EndpointId { get; init; }
    public required string Name { get; init; }
    public required MachineProfile Profile { get; init; }
    public string Status { get; set; } = "Queued";
    public string? Error { get; set; }
    public Dictionary<string, WebDeployPackage> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = new();

    public void AddLog(string line)
    {
        if (Log.Count >= 400) Log.RemoveRange(0, 100);
        Log.Add(line.Length > 400 ? line[..400] + "…" : line);
    }
}

public sealed class WebDeployPackage
{
    public required string Id { get; init; }
    public string Phase { get; set; } = "Queued";
    public int Percent { get; set; } = -1;
    public string Detail { get; set; } = "";
}

/// <summary>The browser console's static files (Web\*.html|js|css|svg, embedded in the Controller).</summary>
public static class WebAssets
{
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, (string Resource, string ContentType)> Routes =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["/"] = ("index.html", "text/html; charset=utf-8"),
            ["/index.html"] = ("index.html", "text/html; charset=utf-8"),
            ["/app.js"] = ("app.js", "text/javascript; charset=utf-8"),
            ["/app.css"] = ("app.css", "text/css; charset=utf-8"),
            ["/favicon.svg"] = ("favicon.svg", "image/svg+xml"),
        };

    /// <summary>
    /// Strict policy: only same-origin scripts / styles / images, no inline script, no framing, no
    /// plugins, no form posts elsewhere.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; " +
        "font-src 'self'; manifest-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'";

    public static byte[]? Get(string resource) => Cache.GetOrAdd(resource, static r =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HomeRms.Web." + r);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    /// <summary>Short opaque id for a saved endpoint (stable for host:port, safe in URLs).</summary>
    public static string EndpointId(MachineProfile m) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{m.Host.ToLowerInvariant()}:{m.Port}:{(int)m.Kind}")))[..12].ToLowerInvariant();
}
