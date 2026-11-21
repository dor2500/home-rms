using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using HomeRms.Shared;
using HomeRms.Shared.Updates;

namespace HomeRms.Agent.Core;

public enum AgentUpdateState { Idle, Checking, UpToDate, Available, Downloading, Applying, Error }

/// <summary>Outcome of one GitHub Releases check.</summary>
public sealed record UpdateCheckResult(
    Version Current,
    string? LatestTag,
    Version? Latest,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt,
    string? AssetUrl,
    long AssetSize,
    bool UpdateAvailable,
    string? Error,
    bool NetworkError = false)
{
    internal string? ChecksumUrl { get; init; }
}

/// <summary>Immutable snapshot of the updater for UIs and the remote status op.</summary>
public sealed record AgentUpdateStatus(
    AgentUpdateState State,
    Version Current,
    UpdateCheckResult? LastResult,
    DateTimeOffset? LastChecked,
    int ProgressPercent,
    string Message,
    bool AutoInstall,
    bool CanSelfUpdate,
    string? SelfUpdateBlockedReason,
    int CheckIntervalHours = AgentUpdateSettings.DefaultIntervalHours);

/// <summary>Persisted updater settings (next to the agent's other per-user settings).</summary>
/// <param name="CheckIntervalHours">How often the background check runs: 24 or 48 hours.</param>
/// <param name="LastAutoCheckUtc">When the background check last reached GitHub, so restarts don't re-check early.</param>
public sealed record AgentUpdateSettings(bool AutoInstall = true, int CheckIntervalHours = AgentUpdateSettings.DefaultIntervalHours,
    DateTimeOffset? LastAutoCheckUtc = null)
{
    public const int DefaultIntervalHours = 24;
    public static readonly int[] AllowedIntervalHours = [24, 48];

    public static int NormalizeInterval(int hours) => AllowedIntervalHours.Contains(hours) ? hours : DefaultIntervalHours;

    /// <summary>True when a background check should run now (never checked, interval elapsed, or the clock went backwards).</summary>
    public static bool IsAutoCheckDue(DateTimeOffset? lastUtc, int intervalHours, DateTimeOffset nowUtc) =>
        lastUtc is not { } last
        || nowUtc - last >= TimeSpan.FromHours(NormalizeInterval(intervalHours))
        || last - nowUtc > TimeSpan.FromHours(1);

    private static string UserPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRMS", "agent-updates.json");

    // Machine-wide mirror so the boot-time SYSTEM instance (whose LocalAppData is the system
    // profile) honours the choice the signed-in user made in the tray UI.
    private static string MachinePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeRMS", "agent-updates.json");

    public static AgentUpdateSettings Load(ActivityLog? log = null)
    {
        // Newest file wins: the SYSTEM instance's own copy must not shadow a newer choice the signed-in
        // user saved (which is mirrored to the machine-wide file), and vice versa.
        var candidates = new[] { UserPath, MachinePath }
            .Where(File.Exists)
            .OrderByDescending(p => { try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; } });
        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                return JsonSerializer.Deserialize<AgentUpdateSettings>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                log?.Write($"[updater] could not read {path}: {ex.Message}");
            }
        }
        return new();
    }

    public void Save(ActivityLog? log = null)
    {
        var json = JsonSerializer.Serialize(this);
        AtomicFile.WriteAllText(UserPath, json);
        try { AtomicFile.WriteAllText(MachinePath, json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Write($"[updater] machine-wide settings mirror not written ({ex.Message}); the boot-time agent keeps its own setting");
        }
    }
}

/// <summary>
/// Checks GitHub Releases (dor2500/home-rms) for a newer HomeRmsAgent.exe, and downloads,
/// verifies (PE header + SHA-256 from checksums.sha256), swaps and restarts the agent.
/// A background loop checks once every 24 or 48 hours (setting; the last check time is persisted so
/// restarts don't re-check early) and installs silently unless "Install updates automatically" is
/// off, in which case it only notifies. Manual "Check for updates" works at any time.
///
/// Dual instances: the installer registers an ONLOGON task (user, elevated) and an ONSTART task
/// (SYSTEM) for the same exe. Only one process at a time may stage/swap: a machine-wide named
/// mutex (Global\HomeRmsAgent.Update) is created for the duration of the update — if it already
/// exists another instance is updating and this one backs off. The swap script stops every other
/// agent process running from the same file (so the copy cannot fail on a locked exe), and
/// restarts the counterpart scheduled task afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentUpdater : IDisposable
{
    public const string AssetName = "HomeRmsAgent.exe";
    private const string LatestReleaseApi = "https://api.github.com/repos/dor2500/home-rms/releases/latest";
    private const string UpdateMutexName = @"Global\HomeRmsAgent.Update";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(6);
    /// <summary>How often the loop wakes to see whether the 24/48 h check is due (cheap, no network).</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    private readonly ActivityLog _log;
    private readonly string[] _launchArgs;
    private readonly string _releaseApi;
    private readonly bool _applyDisabled;
    private readonly string? _applyDisabledReason;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _stateGate = new();
    private int _applying;
    private string? _notifiedTag;

    private AgentUpdateState _state = AgentUpdateState.Idle;
    private UpdateCheckResult? _last;
    private DateTimeOffset? _lastChecked;
    private int _progress;
    private string _message = "Not checked yet.";
    private AgentUpdateSettings _settings;

    /// <summary>Raised (on a worker thread) whenever <see cref="GetStatus"/> changes.</summary>
    public event Action<AgentUpdateStatus>? StatusChanged;

    /// <summary>Raised once per release when the background check finds an update it will not install itself.</summary>
    public event Action<UpdateCheckResult>? UpdateAvailableNotification;

    /// <param name="currentVersionOverride">Test seam: pretend to be this version (never changes the real build).</param>
    /// <param name="applyDisabledReason">When set, checks work but download/apply is refused with this reason (UI preview, tests).</param>
    public AgentUpdater(ActivityLog log, string[] launchArgs, Version? currentVersionOverride = null,
        string? applyDisabledReason = null, string? releaseApi = null)
    {
        _log = log;
        _launchArgs = launchArgs;
        _releaseApi = releaseApi ?? LatestReleaseApi;
        _applyDisabled = applyDisabledReason is not null;
        _applyDisabledReason = applyDisabledReason;
        CurrentVersion = ReleaseVersion.Normalize(currentVersionOverride
            ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
        _settings = AgentUpdateSettings.Load(log);
    }

    public Version CurrentVersion { get; }

    public DateTimeOffset? LastChecked { get { lock (_stateGate) return _lastChecked; } }

    public bool AutoInstall => _settings.AutoInstall;

    public int CheckIntervalHours => AgentUpdateSettings.NormalizeInterval(_settings.CheckIntervalHours);

    public void SetAutoInstall(bool enabled)
    {
        if (_settings.AutoInstall == enabled) return;
        _settings = _settings with { AutoInstall = enabled };
        SaveSettings();
        _log.Write($"[updater] install updates automatically = {enabled}");
        Publish();
    }

    /// <summary>Sets the background check interval (24 or 48 hours; anything else falls back to 24).</summary>
    public void SetCheckIntervalHours(int hours)
    {
        hours = AgentUpdateSettings.NormalizeInterval(hours);
        if (CheckIntervalHours == hours) return;
        _settings = _settings with { CheckIntervalHours = hours };
        SaveSettings();
        _log.Write($"[updater] background update check every {hours} h");
        Publish();
    }

    private void SaveSettings()
    {
        try { _settings.Save(_log); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write($"[updater] could not save update settings: {ex.Message}");
        }
    }

    public AgentUpdateStatus GetStatus()
    {
        var blocked = SelfUpdateBlockedReason();
        lock (_stateGate)
            return new AgentUpdateStatus(_state, CurrentVersion, _last, _lastChecked, _progress, _message,
                _settings.AutoInstall, blocked is null, blocked, CheckIntervalHours);
    }

    /// <summary>Starts the background loop (wakes every 15 min; checks GitHub once the 24/48 h interval is due).</summary>
    public void Start()
    {
        if (_launchArgs.Any(a => string.Equals(a, "--no-auto-update", StringComparison.OrdinalIgnoreCase)))
        {
            _log.Write("[updater] background update checks disabled via --no-auto-update");
            return;
        }
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(StartupDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Re-read so the boot-time SYSTEM instance picks up interval changes made in the tray UI
                var fresh = AgentUpdateSettings.Load(_log);
                _settings = _settings with { CheckIntervalHours = fresh.CheckIntervalHours, AutoInstall = fresh.AutoInstall,
                    LastAutoCheckUtc = Max(_settings.LastAutoCheckUtc, fresh.LastAutoCheckUtc) };

                if (AgentUpdateSettings.IsAutoCheckDue(_settings.LastAutoCheckUtc, CheckIntervalHours, DateTimeOffset.UtcNow))
                {
                    var r = await CheckAsync(ct);
                    if (!r.NetworkError && r.Error is null)
                    {
                        // Only a successful check restarts the 24/48 h clock; offline machines retry on the next tick
                        _settings = _settings with { LastAutoCheckUtc = DateTimeOffset.UtcNow };
                        SaveSettings();
                    }
                    if (r.UpdateAvailable) await OnBackgroundUpdateFoundAsync(r, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Write($"[updater] background update failed: {ex.Message}");
            }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);

    private async Task OnBackgroundUpdateFoundAsync(UpdateCheckResult r, CancellationToken ct)
    {
        var blocked = SelfUpdateBlockedReason();
        var needsAdmin = blocked is null && NeedsElevationForSwap(out _);
        if (_settings.AutoInstall && blocked is null && !needsAdmin)
        {
            _log.Write($"[updater] new release {r.LatestTag} found (current {ReleaseVersion.Display(CurrentVersion)}) — installing automatically");
            await DownloadAndApplyAsync(null, ct);
            return;
        }

        if (_settings.AutoInstall && blocked is null && needsAdmin)
            _log.Write($"[updater] {r.LatestTag} available but installing it needs administrator rights — waiting for the user");
        if (_notifiedTag != r.LatestTag)
        {
            _notifiedTag = r.LatestTag;
            UpdateAvailableNotification?.Invoke(r);
        }
    }

    // ------------------------------------------------------------------ check
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        await _checkGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _applying) == 0)
                SetState(AgentUpdateState.Checking, 0, "Checking GitHub for a newer release…");

            UpdateCheckResult result;
            try
            {
                using var http = CreateHttp(TimeSpan.FromSeconds(20));
                var json = await http.GetStringAsync(_releaseApi, ct);
                result = Evaluate(ReleaseInfo.Parse(json, AssetName), CurrentVersion);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                result = new UpdateCheckResult(CurrentVersion, null, null, "", null, null, 0, false,
                    $"Could not reach GitHub: {ex.Message}", NetworkError: true);
            }
            catch (JsonException ex)
            {
                result = new UpdateCheckResult(CurrentVersion, null, null, "", null, null, 0, false,
                    $"GitHub returned an unreadable response: {ex.Message}");
            }

            lock (_stateGate)
            {
                _last = result;
                _lastChecked = DateTimeOffset.Now;
            }

            if (Volatile.Read(ref _applying) == 0)
            {
                if (result.Error is not null)
                {
                    _log.Write($"[updater] check failed: {result.Error}");
                    SetState(AgentUpdateState.Error, 0, result.Error);
                }
                else if (result.UpdateAvailable)
                {
                    _log.Write($"[updater] {result.LatestTag} is available (current {ReleaseVersion.Display(CurrentVersion)})");
                    SetState(AgentUpdateState.Available, 0, $"Home RMS Agent {result.LatestTag} is available.");
                }
                else
                {
                    SetState(AgentUpdateState.UpToDate, 0,
                        $"You're up to date ({ReleaseVersion.Display(CurrentVersion)} is the latest release).");
                }
            }
            return result;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>Pure decision logic (unit-testable without network).</summary>
    public static UpdateCheckResult Evaluate(ReleaseInfo release, Version current)
    {
        current = ReleaseVersion.Normalize(current);
        var notes = ReleaseNotes.ToPlainText(release.Body);
        if (release.Version is null)
        {
            var why = release.IsPrerelease
                ? $"Latest release '{release.Tag}' is a pre-release; ignored."
                : $"Latest release tag '{release.Tag}' is not a version number.";
            return new UpdateCheckResult(current, release.Tag, null, notes, release.PublishedAt, null, 0, false, why);
        }

        var newer = ReleaseVersion.IsNewer(release.Version, current);
        if (newer && string.IsNullOrWhiteSpace(release.AssetUrl))
            return new UpdateCheckResult(current, release.Tag, release.Version, notes, release.PublishedAt, null, 0, false,
                $"Release {release.Tag} has no {AssetName} asset.");

        return new UpdateCheckResult(current, release.Tag, release.Version, notes, release.PublishedAt,
            release.AssetUrl, release.AssetSize, newer, null) { ChecksumUrl = release.ChecksumUrl };
    }

    // ------------------------------------------------------------------ apply
    /// <summary>
    /// Why this process cannot replace itself at all (null = it can). Independent of elevation.
    /// </summary>
    public string? SelfUpdateBlockedReason()
    {
        if (_applyDisabled) return _applyDisabledReason;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !string.Equals(Path.GetFileName(exe), AssetName, StringComparison.OrdinalIgnoreCase))
            return $"Self-update only works for the published {AssetName} (this process is '{Path.GetFileName(exe)}').";
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "HomeRmsAgent.dll")))
            return null; // single-file publish (normal install)
        return "This is a development build (framework-dependent output); self-update is disabled.";
    }

    /// <summary>
    /// True when the swap needs administrator rights this process does not have: the install
    /// folder is not writable, or another agent instance (e.g. the SYSTEM boot instance or an
    /// elevated one) runs from the same file and cannot be stopped without elevation.
    /// </summary>
    public bool NeedsElevationForSwap(out string reason)
    {
        reason = "";
        if (IsElevated()) return false;
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;
        try
        {
            var probe = Path.Combine(dir, $".hrms-write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"The install folder '{dir}' is not writable without administrator rights.";
            return true;
        }

        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AssetName)))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase) && p.SessionId != Process.GetCurrentProcess().SessionId)
                    {
                        reason = "Another Home RMS Agent instance from a different session uses this file.";
                        return true;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    reason = $"Another Home RMS Agent instance (PID {p.Id}, probably the boot-time SYSTEM instance) must be stopped, which needs administrator rights.";
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Downloads, verifies and stages the latest release, then hands over to a PowerShell swap
    /// script and exits the process. Returns only on failure (the error is also in <see cref="GetStatus"/>).
    /// </summary>
    /// <param name="allowElevationPrompt">When the swap needs admin rights, run the swap step through UAC.</param>
    public async Task DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken ct = default, bool allowElevationPrompt = false)
    {
        var r = _last;
        if (r is null || !r.UpdateAvailable || r.Error is not null)
            r = await CheckAsync(ct);
        if (r.Error is not null) throw new InvalidOperationException(r.Error);
        if (!r.UpdateAvailable) throw new InvalidOperationException($"Already up to date ({ReleaseVersion.Display(CurrentVersion)}).");

        if (Interlocked.CompareExchange(ref _applying, 1, 0) != 0)
            throw new InvalidOperationException("An update is already in progress.");

        Mutex? machineLock = null;
        try
        {
            if (SelfUpdateBlockedReason() is { } blocked) throw new InvalidOperationException(blocked);

            var needsAdmin = NeedsElevationForSwap(out var adminReason);
            if (needsAdmin && !allowElevationPrompt)
                throw new InvalidOperationException($"{adminReason} Run the update from the agent window on this PC (it will ask for administrator approval), or restart the agent elevated.");

            machineLock = TryAcquireMachineLock()
                ?? throw new InvalidOperationException("Another Home RMS Agent instance on this PC is already installing the update.");

            var exePath = Environment.ProcessPath!;
            var updateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRMS", "Update");
            Directory.CreateDirectory(updateDir);
            var stagedExe = Path.Combine(updateDir, $"HomeRmsAgent_{r.LatestTag}.exe");

            SetState(AgentUpdateState.Downloading, 0, $"Downloading {r.LatestTag}…");
            using var http = CreateHttp(TimeSpan.FromMinutes(10));
            var bytes = await DownloadAsync(http, r.AssetUrl!, r.AssetSize, pct =>
            {
                SetState(AgentUpdateState.Downloading, pct, $"Downloading {r.LatestTag}… {pct}%");
                progress?.Report(pct);
            }, ct);

            if (bytes.Length < 100_000 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("The downloaded file is not a valid Windows executable — update aborted.");

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.IsNullOrWhiteSpace(r.ChecksumUrl))
            {
                var checksums = await http.GetStringAsync(r.ChecksumUrl, ct);
                var expectedHash = checksums.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(l => l.Contains(AssetName, StringComparison.OrdinalIgnoreCase))?
                    .Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(expectedHash) && !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 mismatch (expected {expectedHash}, got {actualHash}) — update aborted.");
            }
            else
            {
                _log.Write("[updater] release has no checksums.sha256; relying on the PE header check only");
            }

            await File.WriteAllBytesAsync(stagedExe, bytes, ct);

            SetState(AgentUpdateState.Applying, 100, $"Installing {r.LatestTag} and restarting the agent…");
            var scriptPath = Path.Combine(updateDir, "apply-update.ps1");
            await File.WriteAllTextAsync(scriptPath, BuildSwapScript(stagedExe, exePath, actualHash, Path.Combine(updateDir, "apply-update.log"),
                relaunchUnelevated: needsAdmin), ct);

            _log.Write($"[updater] applying update to {r.LatestTag}{(needsAdmin ? " (elevated swap)" : "")} and restarting agent...");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoLogo -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = needsAdmin,
                CreateNoWindow = !needsAdmin,
            };
            if (needsAdmin)
            {
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
            }
            try
            {
                Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("Administrator approval was declined — the update was not installed.");
            }

            // The machine-wide lock is released by process exit; the swap script waits for it.
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            machineLock?.Dispose();
            Interlocked.Exchange(ref _applying, 0);
            var msg = ex is OperationCanceledException && !ct.IsCancellationRequested ? ex.Message
                : ex is OperationCanceledException ? "Update cancelled."
                : ex.Message;
            _log.Write($"[updater] update failed: {msg}");
            SetState(AgentUpdateState.Error, 0, msg);
            throw;
        }
    }

    /// <summary>Fire-and-forget variant for the remote op; returns a user-facing message immediately.</summary>
    public async Task<(bool Ok, string Message)> StartRemoteUpdateAsync(string? expectedVersion, CancellationToken ct)
    {
        if (SelfUpdateBlockedReason() is { } blocked) return (false, blocked);
        if (Volatile.Read(ref _applying) != 0) return (true, "An update is already in progress; the agent will restart when it finishes.");
        if (NeedsElevationForSwap(out var reason))
            return (false, $"{reason} The agent is not running as Administrator, so it cannot install the update remotely (no UAC prompt is shown for remote requests).");

        var r = await CheckAsync(ct);
        if (r.Error is not null) return (false, r.Error);
        if (!r.UpdateAvailable) return (true, $"Already up to date ({ReleaseVersion.Display(CurrentVersion)}).");
        if (!string.IsNullOrWhiteSpace(expectedVersion) &&
            (!ReleaseVersion.TryParseTag(expectedVersion, out var want) || ReleaseVersion.Normalize(want) != ReleaseVersion.Normalize(r.Latest!)))
            return (false, $"The latest release is {r.LatestTag}, not '{expectedVersion}'. Check the status and try again.");

        _ = Task.Run(async () =>
        {
            try { await DownloadAndApplyAsync(null, _cts.Token); }
            catch (Exception) { /* already logged and surfaced in Status */ }
        });
        return (true, $"Updating from {ReleaseVersion.Display(CurrentVersion)} to {r.LatestTag} in the background. The agent will restart in about a minute; reconnect afterwards.");
    }

    /// <summary>JSON for the agent/update-status remote op.</summary>
    public string StatusJson()
    {
        var s = GetStatus();
        return JsonSerializer.Serialize(new
        {
            current = ReleaseVersion.Display(s.Current),
            latest = s.LastResult?.LatestTag ?? "",
            available = s.LastResult?.UpdateAvailable ?? false,
            lastChecked = s.LastChecked?.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            autoUpdate = s.AutoInstall,
            checkIntervalHours = s.CheckIntervalHours,
            state = s.State.ToString(),
            message = s.Message,
            canSelfUpdate = s.CanSelfUpdate,
        });
    }

    private static async Task<byte[]> DownloadAsync(HttpClient http, string url, long expectedSize, Action<int> onPercent, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = total > 0 && total < int.MaxValue ? new MemoryStream((int)total) : new MemoryStream();
        var buf = new byte[81920];
        var lastPct = -1;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (total > 0)
            {
                var pct = (int)Math.Min(100, ms.Length * 100 / total);
                if (pct != lastPct) { lastPct = pct; onPercent(pct); }
            }
        }
        return ms.ToArray();
    }

    private string BuildSwapScript(string stagedExe, string exePath, string sha256, string logPath, bool relaunchUnelevated)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var pid = Environment.ProcessId;
        var preservedArgs = string.Join(" ", _launchArgs
            .Where(a => !string.Equals(a, "--elevate", StringComparison.OrdinalIgnoreCase))
            .Select(a => $"\"{a.Replace("\"", "\\\"")}\""));
        // The counterpart instance: a SYSTEM agent restarts the user's logon task and vice versa.
        var counterpartTask = IsSystem() ? "HomeRmsAgent" : "HomeRmsAgentBoot";

        string relaunch;
        if (relaunchUnelevated && preservedArgs.Length == 0)
            relaunch = $"Start-Process -FilePath 'explorer.exe' -ArgumentList {Q("\"" + exePath + "\"")}"; // back to the user's normal (unelevated) token
        else
            relaunch = $"Start-Process -FilePath {Q(exePath)}" + (preservedArgs.Length == 0 ? "" : $" -ArgumentList {Q(preservedArgs)}");

        return $$"""
            $ErrorActionPreference = 'Continue'
            $log = {{Q(logPath)}}
            function L($m) { Add-Content -LiteralPath $log -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $m) }
            $exe = {{Q(exePath)}}; $staged = {{Q(stagedExe)}}
            L "swap started (pid {{pid}} -> $exe)"
            for ($i = 0; $i -lt 60; $i++) {
                if (-not (Get-Process -Id {{pid}} -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 250
            }
            # Stop every other agent instance running from the same file so the copy cannot hit a locked exe.
            $others = @(Get-CimInstance Win32_Process -Filter "Name='{{AssetName}}'" -ErrorAction SilentlyContinue |
                Where-Object { $_.ProcessId -ne {{pid}} -and $_.ExecutablePath -and ($_.ExecutablePath -ieq $exe) })
            foreach ($p in $others) { L "stopping other agent instance $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
            $ok = $false
            for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
                try { Copy-Item -LiteralPath $staged -Destination $exe -Force -ErrorAction Stop; $ok = $true }
                catch { L "copy attempt $i failed: $($_.Exception.Message)"; Start-Sleep -Milliseconds 500 }
            }
            if ($ok -and ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ieq '{{sha256}}')) {
                L 'new agent binary installed and verified'
                Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
            } else {
                L 'update NOT installed (copy failed or hash mismatch); restarting the previous binary'
            }
            {{relaunch}}
            if ($others.Count -gt 0) { & schtasks.exe /Run /TN '{{counterpartTask}}' | Out-Null; L 'restarted {{counterpartTask}} task' }
            """;
    }

    /// <summary>
    /// The lock is the existence of the named mutex: the first instance creates it and keeps the
    /// handle until it exits (the swap hand-off), so no thread-affine WaitOne/ReleaseMutex is needed.
    /// An instance running as another account (SYSTEM vs user) may not even be allowed to open it —
    /// that also means "someone else holds it".
    /// </summary>
    public static Mutex? TryAcquireMachineLock()
    {
        try
        {
            var m = new Mutex(false, UpdateMutexName, out var createdNew);
            if (createdNew) return m;
            m.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttp(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRmsAgent-Updater/2.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsSystem()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem;
    }

    private void SetState(AgentUpdateState state, int progress, string message)
    {
        lock (_stateGate)
        {
            _state = state;
            _progress = progress;
            _message = message;
        }
        Publish();
    }

    private void Publish()
    {
        var snapshot = GetStatus();
        StatusChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
