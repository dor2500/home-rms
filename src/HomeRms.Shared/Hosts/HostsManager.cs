using System.Diagnostics;
using System.Net;
using System.Text.Json;
using HomeRms.Shared.Protocol;

namespace HomeRms.Shared.Hosts;

/// <summary>Where the hosts blocker keeps its files. Every path / port is overridable (test seam).</summary>
public sealed class HostsBlockerOptions
{
    /// <summary>Hosts file to manage; null = the system hosts file.</summary>
    public string? HostsPath { get; init; }

    /// <summary>Machine-wide settings + backup directory; null = %ProgramData%\HomeRMS (Windows) or /var/lib/homerms (Linux root).</summary>
    public string? ConfigDir { get; init; }

    public int HttpPort { get; init; } = 80;
    public int HttpsPort { get; init; } = 443;

    /// <summary>Single-instance lock name (named mutex on Windows, lock file name elsewhere); null = machine-wide default.</summary>
    public string? LockName { get; init; }

    /// <summary>Replaces the platform DNS flush (tests); returns one line per step.</summary>
    public Func<IReadOnlyList<string>>? DnsFlush { get; init; }

    /// <summary>null = require admin / root only when the system hosts file is managed.</summary>
    public bool? RequireElevation { get; init; }

    /// <summary>How often settings written by the other agent instance are picked up.</summary>
    public TimeSpan SyncInterval { get; init; } = TimeSpan.FromSeconds(15);

    public string EndpointName { get; init; } = Environment.MachineName;

    /// <summary>Per-domain event debounce window.</summary>
    public TimeSpan EventDebounce { get; init; } = BlockedEventThrottle.DefaultPerDomainWindow;

    public string ResolvedHostsPath => HostsPath ?? HostsFile.SystemPath;

    public string ResolvedConfigDir => ConfigDir ?? DefaultConfigDir;

    public static string DefaultConfigDir =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeRMS")
            : Environment.IsPrivilegedProcess
                ? "/var/lib/homerms"
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeRMS");
}

/// <summary>Persisted blocker settings (blocker.json in the machine-wide config dir). The listener is OFF by default.</summary>
public sealed class BlockerSettings
{
    public bool ListenerEnabled { get; set; }
    public string LoopbackIp { get; set; } = HostsFile.DefaultLoopbackIp;
    public string? LastBackupPath { get; set; }
}

/// <summary>
/// Agent-side hosts file editor, website blocker and optional blocked-access listener (shared by the Windows
/// and Linux agents). Every write: elevation check → hosts.bak + timestamped copy (last 5 kept) → atomic
/// replace (ACLs / mode preserved) → DNS cache flush; each step is reported back to the console.
/// </summary>
public sealed class HostsManager : IDisposable
{
    public const string SettingsFileName = "blocker.json";
    public const int KeepBackups = 5;
    private const string DefaultLockName = "HomeRmsAgent.BlockListener";

    private readonly HostsBlockerOptions _o;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BlockedEventThrottle _throttle;
    private Timer? _syncTimer;
    private SinkholeListener? _listener;
    private IDisposable? _lock;
    private bool _ownedByOther;
    private IReadOnlyList<string> _lastWarnings = [];
    private volatile IReadOnlySet<string> _blocked = new HashSet<string>();
    private DateTime _settingsStamp;
    private DateTime _hostsStamp;
    private bool _disposed;

    public HostsManager(HostsBlockerOptions? options, Action<string> log)
    {
        _o = options ?? new HostsBlockerOptions();
        _log = log;
        _throttle = new BlockedEventThrottle { PerDomainWindow = _o.EventDebounce };
    }

    public AgentEventHub Events { get; } = new();

    public string HostsPath => _o.ResolvedHostsPath;
    private string SettingsPath => Path.Combine(_o.ResolvedConfigDir, SettingsFileName);
    private string BackupDir => Path.Combine(_o.ResolvedConfigDir, "hosts-backups");
    private bool RequireElevation => _o.RequireElevation ?? _o.HostsPath is null;
    private static bool IsElevated => Environment.IsPrivilegedProcess;
    private string NewLineFallback => HostsFile.PlatformNewLine;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Starts the listener only when the persisted settings say so, then follows settings changes.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try { await ReconcileAsync(null, CancellationToken.None); }
            catch (Exception ex) { _log($"[blocker] startup check failed: {ex.Message}"); }
        });
        _syncTimer = new Timer(_ => _ = SyncTickAsync(), null, _o.SyncInterval, _o.SyncInterval);
    }

    private async Task SyncTickAsync()
    {
        try
        {
            var s = File.Exists(SettingsPath) ? File.GetLastWriteTimeUtc(SettingsPath) : DateTime.MinValue;
            var h = File.Exists(HostsPath) ? File.GetLastWriteTimeUtc(HostsPath) : DateTime.MinValue;
            if (s == _settingsStamp && h == _hostsStamp) return;
            if (!await _gate.WaitAsync(0)) return;
            try
            {
                if (s != _settingsStamp) await ReconcileLockedAsync(null);
                else RefreshBlockedSet(ReadText().Decoded.Text);
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex) { _log($"[blocker] settings sync failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _syncTimer?.Dispose();
        StopListener();
    }

    // ------------------------------------------------------------------ read

    public HostsConfig GetConfig()
    {
        var settings = LoadSettings();
        var cfg = new HostsConfig
        {
            Path = HostsPath,
            ListenerEnabled = settings.ListenerEnabled,
            LoopbackIp = settings.LoopbackIp,
            LastBackupPath = settings.LastBackupPath ?? "",
            IsElevated = IsElevated,
            OsFamily = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : Environment.OSVersion.Platform.ToString(),
            Listener = BuildStatus(),
        };
        try
        {
            var (decoded, _) = ReadText();
            var state = HostsFile.ReadManaged(decoded.Text);
            cfg.RawText = decoded.Text;
            cfg.RawHash = HostsFile.HashText(decoded.Text);
            cfg.Encoding = decoded.EncodingName;
            cfg.Newline = HostsFile.DetectNewLine(decoded.Text, NewLineFallback) == "\r\n" ? "CRLF" : "LF";
            cfg.Domains.AddRange(state.Domains);
            cfg.IncludeWww = state.Block.Exists ? state.IncludeWww : true;
            cfg.BlockError = state.Block.Error ?? "";
            cfg.MappingIp = state.MappingIp ?? HostsFile.BlockedIp;
        }
        catch (Exception ex)
        {
            cfg.BlockError = $"The hosts file could not be read: {ex.Message}";
        }
        return cfg;
    }

    private BlockListenerStatus BuildStatus()
    {
        var st = new BlockListenerStatus { HttpPort = _o.HttpPort, HttpsPort = _o.HttpsPort, OwnedByOtherInstance = _ownedByOther };
        var l = _listener;
        if (l is not null)
        {
            st.Running = true;
            st.HttpBound = l.HttpBound;
            st.HttpsBound = l.HttpsBound;
        }
        st.Warnings.AddRange(_lastWarnings);
        st.Summary = _ownedByOther ? "Listener owned by the other agent instance on this PC."
            : l is null ? (LoadSettings().ListenerEnabled ? "Listener enabled but not running." : "Listener off.")
            : !l.HttpBound && !l.HttpsBound ? "Listener could not bind any port; blocked names point to 0.0.0.0."
            : $"Listening on {LoadSettings().LoopbackIp}: HTTP {(l.HttpBound ? "bound" : "NOT bound")} ({_o.HttpPort}), HTTPS {(l.HttpsBound ? "bound" : "NOT bound")} ({_o.HttpsPort}).";
        return st;
    }

    // ------------------------------------------------------------------ mutate

    public async Task<HostsOpResult> SetBlocklistAsync(HostsBlocklistRequest req, string op)
    {
        if (ElevationError() is { } denied) return denied;
        var (domains, errors) = HostsFile.NormalizeDomains(req.Domains);
        if (errors.Count > 0)
            return Fail($"{errors.Count} invalid domain(s); nothing was changed.", errors);

        await _gate.WaitAsync();
        try
        {
            var (decoded, _) = ReadText();
            var before = HostsFile.ReadManaged(decoded.Text);
            if (before.Block.Error is not null)
                return Fail("The Home RMS block in the hosts file is damaged; nothing was changed. Fix it in the raw editor.", [before.Block.Error]);

            var mapping = EffectiveMappingIp(before.MappingIp);
            var newText = HostsFile.ApplyBlock(decoded.Text, domains, req.IncludeWww, mapping, NewLineFallback);
            var (added, removed) = HostsFile.Diff(before.Hosts, HostsFile.ExpandHosts(domains, req.IncludeWww));
            var result = new HostsOpResult { Changed = newText != decoded.Text };
            result.Added.AddRange(added);
            result.Removed.AddRange(removed);

            if (req.DryRun)
            {
                result.Success = true;
                result.Message = result.Changed ? $"{added.Count} hostname(s) would be added, {removed.Count} removed." : "No changes.";
                result.Config = GetConfig();
                return result;
            }

            _log($"[hosts] '{op}' set the block list: {domains.Count} domain(s), include www={req.IncludeWww} (+{added.Count} / -{removed.Count})");
            if (result.Changed) WriteAndFlush(decoded, newText, result);
            else result.Steps.Add("Hosts file already up to date (nothing written).");
            result.Success = true;
            result.Message = result.Changed
                ? $"Block list saved: {domains.Count} domain(s) (+{added.Count} / -{removed.Count} hostnames)."
                : "Block list unchanged.";
            RefreshBlockedSet(newText);
            result.Config = GetConfig();
            return result;
        }
        catch (Exception ex)
        {
            _log($"[hosts] '{op}' block list update FAILED: {ex.Message}");
            return Fail(ex.Message, ex is HostsFormatException hf ? hf.Errors : null);
        }
        finally { _gate.Release(); }
    }

    public async Task<HostsOpResult> SetRawAsync(HostsRawRequest req, string op)
    {
        var errors = HostsFile.ValidateRaw(req.Text);
        if (errors.Count > 0) return Fail($"{errors.Count} problem(s) found; nothing was saved.", errors);
        if (req.ValidateOnly) return new HostsOpResult { Success = true, Message = "No syntax problems found." };
        if (ElevationError() is { } denied) return denied;

        await _gate.WaitAsync();
        try
        {
            var (decoded, _) = ReadText();
            if (!string.Equals(HostsFile.HashText(decoded.Text), req.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                var c = Fail("The hosts file changed on the endpoint since it was loaded. Reload it and re-apply your edits.");
                c.Conflict = true;
                c.Config = GetConfig();
                return c;
            }
            if (!decoded.CanEncode(req.Text))
                return Fail($"The text contains characters that cannot be stored in this file's encoding ({decoded.EncodingName}).");

            var result = new HostsOpResult { Changed = req.Text != decoded.Text };
            var (added, removed) = HostsFile.Diff(HostsFile.ReadManaged(decoded.Text).Hosts, HostsFile.ReadManaged(req.Text).Hosts);
            result.Added.AddRange(added);
            result.Removed.AddRange(removed);
            _log($"[hosts] '{op}' saved the raw hosts file ({req.Text.Length} chars)");
            if (result.Changed) WriteAndFlush(decoded, req.Text, result);
            else result.Steps.Add("No changes (nothing written).");
            RefreshBlockedSet(req.Text);
            result.Success = true;
            result.Message = result.Changed ? "Hosts file saved." : "Hosts file unchanged.";
            result.Config = GetConfig();
            return result;
        }
        catch (Exception ex)
        {
            _log($"[hosts] '{op}' raw save FAILED: {ex.Message}");
            return Fail(ex.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task<HostsOpResult> SetListenerAsync(BlockListenerRequest req, string op)
    {
        if (ElevationError() is { } denied) return denied;
        var ip = string.IsNullOrWhiteSpace(req.LoopbackIp) ? null : req.LoopbackIp.Trim();
        if (ip is not null && !HostsFile.IsLoopbackV4(ip))
            return Fail($"'{ip}' is not a loopback address; use one in 127.0.0.0/8 (for example 127.0.0.1).");

        await _gate.WaitAsync();
        try
        {
            var settings = LoadSettings();
            settings.ListenerEnabled = req.Enabled;
            if (ip is not null) settings.LoopbackIp = ip;
            SaveSettings(settings);
            _log($"[blocker] '{op}' {(req.Enabled ? "ENABLED" : "disabled")} the blocked-access listener ({settings.LoopbackIp})");
            var result = new HostsOpResult();
            await ReconcileLockedAsync(result);
            var st = BuildStatus();
            result.Success = !req.Enabled || st.Running || st.OwnedByOtherInstance;
            result.Message = st.Summary;
            result.Config = GetConfig();
            return result;
        }
        catch (Exception ex)
        {
            _log($"[blocker] '{op}' listener change FAILED: {ex.Message}");
            return Fail(ex.Message);
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ listener

    private async Task ReconcileAsync(HostsOpResult? result, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await ReconcileLockedAsync(result); }
        finally { _gate.Release(); }
    }

    /// <summary>Brings the listener and the hosts mapping in line with the persisted settings (caller holds _gate).</summary>
    private Task ReconcileLockedAsync(HostsOpResult? result)
    {
        var settings = LoadSettings();
        _settingsStamp = File.Exists(SettingsPath) ? File.GetLastWriteTimeUtc(SettingsPath) : DateTime.MinValue;
        var steps = result?.Steps;

        var wantIp = settings.LoopbackIp;
        if (settings.ListenerEnabled && !_disposed)
        {
            if (_listener is not null && !string.Equals(_listener.Ip, wantIp, StringComparison.Ordinal))
            {
                StopListener();
                steps?.Add("Stopped the listener to move it to " + wantIp + ".");
            }
            if (_listener is null)
            {
                _lock ??= TryAcquireLock();
                if (_lock is null)
                {
                    _ownedByOther = true;
                    steps?.Add("Listener owned by the other agent instance on this PC; settings saved for it.");
                }
                else
                {
                    _ownedByOther = false;
                    var l = new SinkholeListener(IPAddress.Parse(wantIp), _o.HttpPort, _o.HttpsPort,
                        host => SinkholeProtocol.MatchBlocked(host, _blocked), OnHit, _log);
                    l.Start();
                    _listener = l;
                    _lastWarnings = l.Warnings;
                    steps?.Add($"Listener on {wantIp}: HTTP {_o.HttpPort} {(l.HttpBound ? "bound" : "NOT bound")}, HTTPS {_o.HttpsPort} {(l.HttpsBound ? "bound" : "NOT bound")}.");
                    foreach (var w in l.Warnings)
                    {
                        steps?.Add("WARNING: " + w);
                        PublishWarning(w);
                    }
                    if (!l.HttpBound && !l.HttpsBound)
                        PublishWarning("The blocked-access listener could not bind any port; blocked sites stay blocked via 0.0.0.0 but no alerts will be raised.");
                }
            }
        }
        else
        {
            if (_listener is not null) steps?.Add("Listener stopped; ports released.");
            StopListener();
            _ownedByOther = false;
        }

        // Point the managed entries at the listener only while it really accepts connections.
        if (!_ownedByOther) EnsureMapping(EffectiveMappingIp(null), result);
        else RefreshBlockedSet(SafeReadText());
        return Task.CompletedTask;
    }

    private void StopListener()
    {
        var l = _listener;
        _listener = null;
        l?.Dispose();
        _lastWarnings = [];
        _lock?.Dispose();
        _lock = null;
    }

    private string EffectiveMappingIp(string? currentMapping)
    {
        var l = _listener;
        if (l is not null && (l.HttpBound || l.HttpsBound)) return l.Ip;
        // The other instance runs the listener: keep its loopback mapping.
        if (_ownedByOther && HostsFile.IsLoopbackV4(currentMapping)) return currentMapping!;
        return HostsFile.BlockedIp;
    }

    private void EnsureMapping(string mapping, HostsOpResult? result)
    {
        try
        {
            var (decoded, _) = ReadText();
            var state = HostsFile.ReadManaged(decoded.Text);
            RefreshBlockedSet(decoded.Text);
            if (!state.Block.Exists || state.Hosts.Count == 0 || state.MappingIp == mapping) return;
            if (RequireElevation && !IsElevated)
            {
                _log("[blocker] hosts entries need re-pointing to " + mapping + " but the agent is not elevated");
                return;
            }
            var newText = HostsFile.ApplyBlock(decoded.Text, state.Domains, state.IncludeWww, mapping, NewLineFallback);
            if (newText == decoded.Text) return;
            var r = result ?? new HostsOpResult();
            r.Steps.Add($"Managed entries now point to {mapping}.");
            WriteAndFlush(decoded, newText, r);
            if (result is null) foreach (var s in r.Steps) _log("[blocker] " + s);
            RefreshBlockedSet(newText);
        }
        catch (Exception ex)
        {
            result?.Steps.Add("Could not update the managed entries: " + ex.Message);
            _log("[blocker] could not update the managed entries: " + ex.Message);
        }
    }

    private void OnHit(SinkholeHit hit)
    {
        if (!_throttle.ShouldEmit(_o.EndpointName, hit.Domain)) return;
        _log($"[blocker] blocked {hit.Protocol} attempt to {hit.Domain}");
        Events.Publish(new AgentEvent
        {
            BlockedSite = new BlockedSiteAttempt
            {
                EndpointName = _o.EndpointName,
                Domain = hit.Domain,
                Protocol = hit.Protocol,
                TimestampUnixMs = hit.Time.ToUnixTimeMilliseconds(),
            }
        });
    }

    private void PublishWarning(string message) =>
        Events.Publish(new AgentEvent
        {
            Warning = new AgentWarning { Source = "blocker", Message = message, TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
        });

    private void RefreshBlockedSet(string? text)
    {
        if (text is null) return;
        _blocked = HostsFile.ReadManaged(text).Hosts.ToHashSet(StringComparer.Ordinal);
        _hostsStamp = File.Exists(HostsPath) ? File.GetLastWriteTimeUtc(HostsPath) : DateTime.MinValue;
    }

    private IDisposable? TryAcquireLock()
    {
        var name = _o.LockName ?? DefaultLockName;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Global\ so the SYSTEM (session 0) and logon instances see the same object.
                var m = new Mutex(false, @"Global\" + name, out var createdNew);
                if (createdNew) return m;
                m.Dispose();
                return null;
            }
            catch (UnauthorizedAccessException) { return null; }
        }
        try
        {
            Directory.CreateDirectory(_o.ResolvedConfigDir);
            return new FileStream(Path.Combine(_o.ResolvedConfigDir, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ file I/O

    private (HostsFile.Decoded Decoded, byte[] Bytes) ReadText()
    {
        var bytes = File.Exists(HostsPath) ? File.ReadAllBytes(HostsPath) : [];
        return (HostsFile.Decode(bytes), bytes);
    }

    private string? SafeReadText()
    {
        try { return ReadText().Decoded.Text; } catch { return null; }
    }

    /// <summary>Backup → atomic replace → DNS flush. Throws on write failure (the original stays in place).</summary>
    private void WriteAndFlush(HostsFile.Decoded decoded, string newText, HostsOpResult result)
    {
        var path = HostsPath;
        var bytes = decoded.Encode(newText);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        if (File.Exists(path))
        {
            var bak = Path.Combine(dir, Path.GetFileName(path) + ".bak");
            File.Copy(path, bak, overwrite: true);
            result.Steps.Add($"Backup written: {bak}");
            result.BackupPath = bak;
            try
            {
                Directory.CreateDirectory(BackupDir);
                var stamped = Path.Combine(BackupDir, $"hosts-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak");
                File.Copy(path, stamped, overwrite: true);
                foreach (var old in new DirectoryInfo(BackupDir).GetFiles("hosts-*.bak").OrderByDescending(f => f.Name).Skip(KeepBackups))
                    old.Delete();
                result.Steps.Add($"Timestamped copy kept: {stamped} (last {KeepBackups} kept)");
            }
            catch (Exception ex)
            {
                result.Steps.Add("Timestamped backup copy skipped: " + ex.Message);
            }
            var settings = LoadSettings();
            settings.LastBackupPath = bak;
            TrySaveSettings(settings);
        }

        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.homerms-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                if (!OperatingSystem.IsWindows())
                {
                    try { File.SetUnixFileMode(tmp, File.GetUnixFileMode(path)); } catch { }
                }
                try
                {
                    // File.Replace keeps the destination's ACL / attributes on Windows.
                    File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    result.Steps.Add($"Hosts file written atomically ({bytes.Length:N0} bytes, {decoded.EncodingName}).");
                }
                catch (IOException) when (!OperatingSystem.IsWindows())
                {
                    // /etc/hosts bind-mounted (containers): rename is impossible, rewrite in place instead.
                    File.WriteAllBytes(path, bytes);
                    result.Steps.Add($"Hosts file rewritten in place ({bytes.Length:N0} bytes; atomic rename not possible on this mount).");
                }
            }
            else
            {
                File.Move(tmp, path);
                result.Steps.Add($"Hosts file created ({bytes.Length:N0} bytes).");
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        foreach (var s in (_o.DnsFlush ?? FlushDns)()) result.Steps.Add(s);
        _hostsStamp = File.GetLastWriteTimeUtc(path);
    }

    public static IReadOnlyList<string> FlushDns()
    {
        if (OperatingSystem.IsWindows())
        {
            var ipconfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe");
            return [Run(ipconfig, "/flushdns", "DNS cache flushed (ipconfig /flushdns)")];
        }

        if (FindTool("resolvectl") is { } resolvectl)
        {
            var r = Run(resolvectl, "flush-caches", "DNS cache flushed (resolvectl flush-caches)");
            if (!r.Contains("failed", StringComparison.OrdinalIgnoreCase)) return [r];
        }
        if (FindTool("systemd-resolve") is { } sr)
        {
            var r = Run(sr, "--flush-caches", "DNS cache flushed (systemd-resolve --flush-caches)");
            if (!r.Contains("failed", StringComparison.OrdinalIgnoreCase)) return [r];
        }
        var steps = new List<string>();
        if (FindTool("nscd") is { } nscd) steps.Add(Run(nscd, "-i hosts", "nscd hosts cache invalidated (nscd -i hosts)"));
        if (FindTool("systemctl") is { } systemctl && RunExit(systemctl, "is-active --quiet dnsmasq") == 0)
            steps.Add(Run(systemctl, "restart dnsmasq", "dnsmasq restarted"));
        if (steps.Count == 0) steps.Add("No DNS cache service found (nothing to flush; new lookups read the hosts file directly).");
        return steps;
    }

    private static string? FindTool(string name)
    {
        foreach (var d in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin" })
        {
            var p = Path.Combine(d, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static int RunExit(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null) return -1;
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }

    private static string Run(string file, string args, string okText)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null) return $"DNS flush failed: could not start {Path.GetFileName(file)}";
            var err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return $"DNS flush failed: {Path.GetFileName(file)} timed out"; }
            return p.ExitCode == 0 ? okText : $"DNS flush failed: {Path.GetFileName(file)} {args} exited with {p.ExitCode} {err.Result.Trim()}".TrimEnd();
        }
        catch (Exception ex)
        {
            return $"DNS flush failed: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ settings

    public BlockerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<BlockerSettings>(File.ReadAllText(SettingsPath));
                if (s is not null)
                {
                    if (!HostsFile.IsLoopbackV4(s.LoopbackIp)) s.LoopbackIp = HostsFile.DefaultLoopbackIp;
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[blocker] {SettingsFileName} unreadable ({ex.Message}); listener stays off");
        }
        return new BlockerSettings();
    }

    private void SaveSettings(BlockerSettings s)
    {
        AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        _settingsStamp = File.GetLastWriteTimeUtc(SettingsPath);
    }

    private void TrySaveSettings(BlockerSettings s)
    {
        try { SaveSettings(s); } catch (Exception ex) { _log($"[blocker] could not save {SettingsFileName}: {ex.Message}"); }
    }

    private HostsOpResult? ElevationError() =>
        RequireElevation && !IsElevated
            ? Fail(OperatingSystem.IsWindows()
                ? "Editing the hosts file needs administrator rights. Restart the Home RMS agent elevated (installed as Administrator / --elevate)."
                : "Editing /etc/hosts needs root. Run the Home RMS agent as root (systemd service).")
            : null;

    private static HostsOpResult Fail(string message, IEnumerable<string>? errors = null)
    {
        var r = new HostsOpResult { Success = false, Message = message };
        if (errors is not null) r.Errors.AddRange(errors);
        return r;
    }
}
