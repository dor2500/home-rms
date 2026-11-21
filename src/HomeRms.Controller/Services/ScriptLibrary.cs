using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Services;

[JsonConverter(typeof(JsonStringEnumConverter<ScriptShell>))]
public enum ScriptShell
{
    PowerShell = 0,
    Cmd = 1,
    /// <summary>bash on Linux agents (Windows endpoints skip these snippets).</summary>
    Bash = 2,
}

/// <summary>A saved script snippet.</summary>
public sealed class ScriptSnippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public ScriptShell Shell { get; set; } = ScriptShell.PowerShell;
    public string Body { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool IsBuiltIn { get; set; }

    [JsonIgnore] public string TagsText => string.Join(", ", Tags);
    [JsonIgnore] public string ShellText => ShellName(Shell);

    /// <summary>True when the snippet runs on Linux agents (bash) instead of Windows (PowerShell / cmd).</summary>
    [JsonIgnore] public bool IsLinux => Shell == ScriptShell.Bash;

    public static string ShellName(ScriptShell shell) => shell switch
    {
        ScriptShell.Cmd => "cmd",
        ScriptShell.Bash => "bash",
        _ => "PowerShell",
    };

    public ScriptSnippet Clone() => new()
    {
        Id = Id, Name = Name, Description = Description, Shell = Shell, Body = Body,
        Tags = Tags.ToList(), IsBuiltIn = IsBuiltIn
    };
}

/// <summary>Persists the script library to %AppData%\HomeRMS\script-library.json (built-ins are always present).</summary>
public static class ScriptLibraryStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeRMS", "script-library.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<ScriptSnippet> Load()
    {
        var list = new List<ScriptSnippet>();
        try
        {
            if (File.Exists(FilePath))
                list = JsonSerializer.Deserialize<List<ScriptSnippet>>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex)
        {
            // Corrupt file: start from the built-ins; keep a copy because the next save overwrites it.
            var backup = HomeRms.Shared.AtomicFile.PreserveCorrupt(FilePath);
            LogBus.Publish(LogBus.Scripts, "Library",
                $"script-library.json could not be read ({ex.Message}); only built-in snippets are loaded." +
                (backup is null ? "" : $" The unreadable file was kept as {Path.GetFileName(backup)}."), Themes.PaletteTone.Warning);
        }

        // Built-ins always reflect the shipped version; user snippets are kept as-is.
        list.RemoveAll(s => s.IsBuiltIn);
        list.InsertRange(0, BuiltIns());
        return list;
    }

    public static void Save(IEnumerable<ScriptSnippet> snippets)
    {
        try
        {
            HomeRms.Shared.AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(snippets.ToList(), Options));
        }
        catch (Exception ex)
        {
            // The in-memory library stays usable; surface the failure instead of losing edits silently.
            LogBus.Publish(LogBus.Scripts, "Library", $"Could not save script-library.json: {ex.Message}", Themes.PaletteTone.Warning);
        }
    }

    private static ScriptSnippet B(string id, string name, string desc, ScriptShell shell, string body, params string[] tags) =>
        new() { Id = "builtin-" + id, Name = name, Description = desc, Shell = shell, Body = body, Tags = tags.ToList(), IsBuiltIn = true };

    public static List<ScriptSnippet> BuiltIns() =>
    [
        B("clear-temp", "Clear Temp", "Deletes files older than 1 day from the user and Windows temp folders.", ScriptShell.PowerShell,
            """
            $cut = (Get-Date).AddDays(-1)
            $total = 0
            foreach ($dir in @($env:TEMP, "$env:SystemRoot\Temp")) {
              if (-not (Test-Path $dir)) { continue }
              Get-ChildItem -Path $dir -Recurse -Force -File -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTime -lt $cut } |
                ForEach-Object { try { $total += $_.Length; Remove-Item $_.FullName -Force -ErrorAction Stop } catch { } }
            }
            "Freed approximately {0:N1} MB" -f ($total / 1MB)
            """, "cleanup", "disk"),
        B("flush-dns", "Flush DNS", "Clears the DNS resolver cache.", ScriptShell.Cmd,
            "ipconfig /flushdns", "network"),
        B("sfc", "SFC scan", "System File Checker: verifies and repairs protected system files (needs admin, can take 10+ min).", ScriptShell.Cmd,
            "sfc /scannow", "repair", "admin"),
        B("dism", "DISM RestoreHealth", "Repairs the Windows component store (needs admin, can take a long time).", ScriptShell.Cmd,
            "DISM /Online /Cleanup-Image /RestoreHealth", "repair", "admin"),
        B("spooler", "Restart Print Spooler", "Restarts the Print Spooler service and clears stuck jobs.", ScriptShell.PowerShell,
            """
            Stop-Service -Name Spooler -Force
            Remove-Item "$env:SystemRoot\System32\spool\PRINTERS\*" -Force -ErrorAction SilentlyContinue
            Start-Service -Name Spooler
            Get-Service -Name Spooler | Select-Object Name, Status | Format-Table -AutoSize
            """, "printing", "service"),
        B("ipconfig", "IP config", "Shows the full IP configuration of all adapters.", ScriptShell.Cmd,
            "ipconfig /all", "network", "info"),
        B("disk-report", "Disk cleanup report", "Reports free space per fixed disk and the size of common cleanup locations (read-only).", ScriptShell.PowerShell,
            """
            Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" |
              Select-Object DeviceID, @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}}, @{n='FreeGB';e={[math]::Round($_.FreeSpace/1GB,1)}}, @{n='Free%';e={[math]::Round($_.FreeSpace*100/$_.Size,1)}} |
              Format-Table -AutoSize
            $paths = @($env:TEMP, "$env:SystemRoot\Temp", "$env:SystemRoot\SoftwareDistribution\Download", "$env:SystemRoot\Logs", "$env:LOCALAPPDATA\CrashDumps")
            foreach ($p in $paths) {
              if (Test-Path $p) {
                $s = (Get-ChildItem $p -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
                "{0,-60} {1,10:N1} MB" -f $p, ($s / 1MB)
              }
            }
            "Recycle Bin items: " + ((New-Object -ComObject Shell.Application).NameSpace(10).Items().Count)
            """, "disk", "report"),
        B("reset-wu", "Reset Windows Update components", "Stops update services, renames SoftwareDistribution / catroot2 and restarts the services (needs admin).", ScriptShell.PowerShell,
            """
            $svcs = 'wuauserv','cryptSvc','bits','msiserver'
            Stop-Service -Name $svcs -Force -ErrorAction SilentlyContinue
            $stamp = Get-Date -Format 'yyyyMMddHHmmss'
            if (Test-Path "$env:SystemRoot\SoftwareDistribution") { Rename-Item "$env:SystemRoot\SoftwareDistribution" "SoftwareDistribution.old-$stamp" }
            if (Test-Path "$env:SystemRoot\System32\catroot2") { Rename-Item "$env:SystemRoot\System32\catroot2" "catroot2.old-$stamp" }
            Start-Service -Name $svcs -ErrorAction SilentlyContinue
            Get-Service -Name $svcs | Select-Object Name, Status | Format-Table -AutoSize
            "Windows Update components reset."
            """, "windows update", "repair", "admin"),

        // ---- Linux agents (bash) ----
        B("linux-disk", "Disk usage (Linux)", "Free space per file system and the largest directories under /var and /home.", ScriptShell.Bash,
            """
            df -hT -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null || df -h
            echo
            echo "Largest directories:"
            du -xh --max-depth=1 /var /home 2>/dev/null | sort -rh | head -n 15
            """, "linux", "disk", "report"),
        B("linux-top", "Top processes (Linux)", "The 15 processes using the most CPU and the most memory.", ScriptShell.Bash,
            """
            echo "== By CPU =="
            ps -eo pid,user,%cpu,%mem,etime,comm --sort=-%cpu | head -n 16
            echo
            echo "== By memory =="
            ps -eo pid,user,%cpu,%mem,rss,comm --sort=-%mem | head -n 16
            echo
            uptime
            """, "linux", "performance", "info"),
        B("linux-journal-errors", "Journal errors (Linux)", "Errors and worse from the systemd journal of the current boot, plus failed units.", ScriptShell.Bash,
            """
            if command -v journalctl >/dev/null 2>&1; then
              journalctl -b -p err --no-pager -n 80 2>&1
            else
              tail -n 80 /var/log/syslog 2>/dev/null || tail -n 80 /var/log/messages
            fi
            echo
            echo "== Failed units =="
            systemctl --failed --no-pager 2>/dev/null || true
            """, "linux", "logs", "diagnostics"),
        B("linux-update-check", "Update check (apt / dnf)", "Lists pending package updates (apt, dnf, yum, zypper or pacman) without installing anything.", ScriptShell.Bash,
            """
            if command -v apt-get >/dev/null 2>&1; then
              apt-get update -qq >/dev/null 2>&1 || echo "(apt-get update needs root; showing cached lists)"
              apt list --upgradable 2>/dev/null
            elif command -v dnf >/dev/null 2>&1; then
              dnf -q check-update
            elif command -v yum >/dev/null 2>&1; then
              yum -q check-update
            elif command -v zypper >/dev/null 2>&1; then
              zypper --non-interactive list-updates
            elif command -v pacman >/dev/null 2>&1; then
              checkupdates 2>/dev/null || pacman -Qu
            else
              echo "No supported package manager found."
            fi
            [ -f /var/run/reboot-required ] && echo "A reboot is required."
            true
            """, "linux", "updates"),
        B("linux-restart-service", "Restart a service (Linux)", "Restarts the systemd service named in SERVICE (edit a copy) and shows its status.", ScriptShell.Bash,
            """
            SERVICE="cron.service"
            systemctl restart -- "$SERVICE" && echo "Restarted $SERVICE"
            systemctl status --no-pager -n 10 -- "$SERVICE"
            """, "linux", "service"),
        B("linux-network", "Network info (Linux)", "Addresses, routes, DNS servers and listening ports.", ScriptShell.Bash,
            """
            echo "== Addresses =="; ip -br addr 2>/dev/null || ifconfig -a
            echo; echo "== Routes =="; ip route 2>/dev/null || route -n
            echo; echo "== DNS =="; (resolvectl dns 2>/dev/null || grep -E '^nameserver' /etc/resolv.conf)
            echo; echo "== Listening ports =="; (ss -tulpn 2>/dev/null || netstat -tulpn 2>/dev/null) | head -n 40
            """, "linux", "network", "info"),
    ];
}

public enum ScriptRunState { Queued, Pairing, Running, Succeeded, Failed, Cancelled, Skipped }

/// <summary>One endpoint's execution of a script (observable for the result grid).</summary>
public sealed class ScriptRun : INotifyPropertyChanged
{
    private readonly StringBuilder _output = new();
    private ScriptRunState _state = ScriptRunState.Queued;
    private string _summary = "Queued";
    private TimeSpan _duration;

    public required MachineProfile Endpoint { get; init; }
    public string EndpointName => Endpoint.DisplayTitle;

    public ScriptRunState State { get => _state; set { _state = value; Raise(); Raise(nameof(StateText)); Raise(nameof(IsFinished)); } }
    public string StateText => _state.ToString().ToUpperInvariant();
    public bool IsFinished => _state is ScriptRunState.Succeeded or ScriptRunState.Failed or ScriptRunState.Cancelled or ScriptRunState.Skipped;
    public string Summary { get => _summary; set { _summary = value; Raise(); } }
    public TimeSpan Duration { get => _duration; set { _duration = value; Raise(); Raise(nameof(DurationText)); } }
    public string DurationText => _duration.TotalSeconds < 0.05 ? "" : $"{_duration.TotalSeconds:0.0}s";
    public string Output => _output.ToString();
    public int? ExitCode { get; set; }

    public void Append(string text)
    {
        if (_output.Length > 2_000_000) return; // keep the UI responsive on runaway output
        _output.Append(text);
        Raise(nameof(Output));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// Runs a script on many endpoints at once (bounded parallelism) over the agent Shell RPC, or over
/// WinRM for agentless Windows endpoints. Agents are paired via GetAgentInfo first; the script text
/// is sent in plain text (inside a PowerShell here-string) so it stays readable in the agent's
/// visible activity log.
/// </summary>
public sealed class ScriptExecutor(AgentConnectionPool pool, AgentlessService agentless, Dispatcher dispatcher)
{
    public const string DoneMarker = "__HRMS_DONE__|";

    public int MaxParallel { get; set; } = 4;

    /// <summary>Creates one <see cref="ScriptRun"/> per target (returned immediately) and starts executing them.</summary>
    public (IReadOnlyList<ScriptRun> Runs, Task Completion) Start(
        IEnumerable<MachineProfile> targets, ScriptShell shell, string body, string title, TimeSpan timeout, CancellationToken ct)
    {
        var runs = targets.Select(t => new ScriptRun { Endpoint = t }).ToList();
        var gate = new SemaphoreSlim(Math.Max(1, MaxParallel));
        var task = Task.WhenAll(runs.Select(async r =>
        {
            await gate.WaitAsync(CancellationToken.None);
            try { await RunOneAsync(r, shell, body, title, timeout, ct); }
            finally { gate.Release(); }
        }));
        return (runs, task);
    }

    private void Ui(Action a)
    {
        if (dispatcher.CheckAccess()) a();
        else dispatcher.Invoke(a);
    }

    private async Task RunOneAsync(ScriptRun run, ScriptShell shell, string body, string title, TimeSpan timeout, CancellationToken outer)
    {
        var m = run.Endpoint;
        var started = DateTime.UtcNow;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        var ct = cts.Token;

        try
        {
            if (outer.IsCancellationRequested)
            {
                Ui(() => { run.State = ScriptRunState.Cancelled; run.Summary = "Cancelled before start"; });
                return;
            }

            var linux = m.IsLinuxAgent;
            if (linux && shell != ScriptShell.Bash)
            {
                Ui(() => { run.State = ScriptRunState.Skipped; run.Summary = $"Skipped: {ScriptSnippet.ShellName(shell)} snippet is Windows-only (Linux endpoint, use a bash snippet)"; });
                return;
            }
            if (!linux && shell == ScriptShell.Bash)
            {
                Ui(() => { run.State = ScriptRunState.Skipped; run.Summary = "Skipped: bash snippet runs on Linux agents only"; });
                return;
            }
            if (!linux && m.IsAppliance)
            {
                Ui(() => { run.State = ScriptRunState.Skipped; run.Summary = "Not an agent endpoint (appliance / SSH)"; });
                return;
            }

            var wrapped = linux ? BuildBash(body, title) : BuildPowerShell(shell, body, title);

            if (m.IsAgentless)
            {
                Ui(() => { run.State = ScriptRunState.Running; run.Summary = "Running over WinRM"; });
                var (ok, output) = await agentless.RunShellAsync(m, wrapped, ct, timeoutMs: Math.Max(1, (int)timeout.TotalMilliseconds));
                Ui(() => Finish(run, output, transportOk: ok));
                return;
            }

            Ui(() => { run.State = ScriptRunState.Pairing; run.Summary = "Pairing (GetAgentInfo)"; });
            var conn = await pool.GetPairedAsync(m, interactive: false, ct);

            Ui(() => { run.State = ScriptRunState.Running; run.Summary = "Running"; });
            using var call = conn.Client.Shell(cancellationToken: ct);
            await call.RequestStream.WriteAsync(new ShellInput { Data = wrapped }, ct);
            await call.RequestStream.WriteAsync(new ShellInput { Close = true }, ct);
            await call.RequestStream.CompleteAsync();

            const string markerPrefix = "__HRMS_DONE__";
            var holdBack = markerPrefix.Length - 1;
            var all = new StringBuilder();
            var pending = "";
            var doneSeen = false;
            await foreach (var o in call.ResponseStream.ReadAllAsync(ct))
            {
                all.Append(o.Data);
                if (doneSeen) continue;

                pending += o.Data;
                var cut = pending.IndexOf(markerPrefix, StringComparison.Ordinal);
                if (cut >= 0)
                {
                    if (cut > 0)
                    {
                        var emit = pending[..cut];
                        Ui(() => run.Append(emit));
                    }
                    pending = "";
                    doneSeen = true;
                }
                else if (pending.Length > holdBack)
                {
                    var safeLen = pending.Length - holdBack;
                    var emit = pending[..safeLen];
                    pending = pending[safeLen..];
                    Ui(() => run.Append(emit));
                }
            }
            if (!doneSeen && pending.Length > 0)
                Ui(() => run.Append(pending));
            Ui(() => Finish(run, all.ToString(), transportOk: true, alreadyStreamed: true));
        }
        catch (OperationCanceledException) when (outer.IsCancellationRequested)
        {
            Ui(() => { run.State = ScriptRunState.Cancelled; run.Summary = "Cancelled"; });
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is RpcException { StatusCode: StatusCode.Cancelled or StatusCode.DeadlineExceeded })
        {
            Ui(() =>
            {
                run.State = outer.IsCancellationRequested ? ScriptRunState.Cancelled : ScriptRunState.Failed;
                run.Summary = outer.IsCancellationRequested ? "Cancelled" : $"Timed out after {timeout.TotalMinutes:0} min";
            });
        }
        catch (Exception ex)
        {
            if (ex is RpcException { StatusCode: StatusCode.PermissionDenied }) pool.MarkUnpaired(m);
            Ui(() =>
            {
                run.State = ScriptRunState.Failed;
                run.Summary = AgentErrors.Describe(ex);
                run.Append("[ERROR] " + AgentErrors.Describe(ex) + Environment.NewLine);
            });
        }
        finally
        {
            Ui(() => run.Duration = DateTime.UtcNow - started);
        }
    }

    private static void Finish(ScriptRun run, string output, bool transportOk, bool alreadyStreamed = false)
    {
        var markerIdx = output.LastIndexOf(DoneMarker, StringComparison.Ordinal);
        var clean = markerIdx >= 0 ? output[..markerIdx] : output;
        if (!alreadyStreamed) run.Append(clean);

        if (markerIdx < 0)
        {
            run.State = ScriptRunState.Failed;
            run.Summary = transportOk ? "No completion marker (shell ended early)" : "Remote execution failed";
            return;
        }

        var parts = output[(markerIdx + DoneMarker.Length)..].Split(['|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var hadError = parts.Length > 0 && parts[0].Trim() == "1";
        var exit = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var code) ? code : 0;
        run.ExitCode = exit;
        var ok = transportOk && !hadError && exit == 0;
        run.State = ok ? ScriptRunState.Succeeded : ScriptRunState.Failed;
        var lastLine = clean.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        run.Summary = ok
            ? (lastLine.Length > 0 ? lastLine : "Completed")
            : hadError ? "Completed with errors" + (exit != 0 ? $" (exit {exit})" : "") : $"Exit code {exit}";
    }

    /// <summary>
    /// bash counterpart of <see cref="BuildPowerShell"/> for Linux agents (whose Shell RPC is <c>bash -s</c>):
    /// the body is written to a temp file through a quoted here-document (no expansion, stays readable in the
    /// agent's activity log), run with stdin detached so it cannot swallow the rest of the session, and the
    /// same completion marker (error flag + exit code) is printed last. A body containing the random
    /// delimiter line falls back to Base64.
    /// </summary>
    public static string BuildBash(string body, string title)
    {
        var normalized = body.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        var delimiter = "HRMS_EOF_" + Guid.NewGuid().ToString("N");
        var safeTitle = title.Replace("\r", " ").Replace("\n", " ");
        var sb = new StringBuilder();
        sb.Append("# Home RMS Script Library: ").Append(safeTitle).Append(" (bash)\n");
        sb.Append("__hrms_f=$(mktemp \"${TMPDIR:-/tmp}/hrms_XXXXXXXX.sh\") || { echo \"" + DoneMarker + "1|1\"; exit 1; }\n");
        if (normalized.Split('\n').Any(l => l == delimiter))
            sb.Append("printf '%s' '").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized + "\n"))).Append("' | base64 -d > \"$__hrms_f\"\n");
        else
            sb.Append("cat > \"$__hrms_f\" <<'").Append(delimiter).Append("'\n").Append(normalized).Append('\n').Append(delimiter).Append('\n');
        sb.Append("bash \"$__hrms_f\" < /dev/null 2>&1; __hrms_rc=$?; rm -f -- \"$__hrms_f\"\n");
        sb.Append("echo \"" + DoneMarker + "$([ $__hrms_rc -eq 0 ] && echo 0 || echo 1)|$__hrms_rc\"\n");
        return sb.ToString();
    }

    /// <summary>
    /// Wraps the snippet so multi-line bodies run as one unit, errors are captured and a completion
    /// marker (error flag + exit code) is printed last.
    /// </summary>
    public static string BuildPowerShell(ScriptShell shell, string body, string title)
    {
        var normalized = body.Replace("\r\n", "\n").TrimEnd().Replace("\n", "\r\n");
        // A line starting with '@ (PowerShell 7 also accepts leading whitespace) would end the here-string
        // early: fall back to Base64 in that case.
        var hereStringSafe = !normalized.Split("\r\n").Any(l => l.TrimStart().StartsWith("'@", StringComparison.Ordinal));
        var load = hereStringSafe
            ? "$__hrmsBody = @'\r\n" + normalized + "\r\n'@\r\n"
            : "$__hrmsBody = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
              Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized)) + "'))\r\n";

        var safeTitle = title.Replace("\r", " ").Replace("\n", " ");
        var sb = new StringBuilder();
        sb.Append("# Home RMS Script Library: ").Append(safeTitle).Append(" (").Append(shell == ScriptShell.Cmd ? "cmd" : "PowerShell").Append(")\r\n");
        sb.Append(load);
        sb.Append("$__hrmsErr = $false; $global:LASTEXITCODE = 0\r\n");
        if (shell == ScriptShell.Cmd)
        {
            sb.Append("$__hrmsFile = Join-Path $env:TEMP ('hrms_' + [guid]::NewGuid().ToString('N') + '.cmd')\r\n");
            sb.Append("try { [IO.File]::WriteAllText($__hrmsFile, \"@echo off`r`n\" + $__hrmsBody, [Text.Encoding]::Default); & cmd.exe /d /c $__hrmsFile 2>&1 | ForEach-Object { \"$_\" } } ");
            sb.Append("catch { $__hrmsErr = $true; \"ERROR: $($_.Exception.Message)\" } finally { Remove-Item $__hrmsFile -Force -ErrorAction SilentlyContinue }\r\n");
        }
        else
        {
            sb.Append("try { & ([ScriptBlock]::Create($__hrmsBody)) 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $__hrmsErr = $true; \"ERROR: $_\" } else { $_ } } | Out-String -Stream -Width 200 } ");
            sb.Append("catch { $__hrmsErr = $true; \"ERROR: $($_.Exception.Message)\" }\r\n");
        }
        sb.Append("\"" + DoneMarker + "$([int]$__hrmsErr)|$LASTEXITCODE\"\r\n");
        return sb.ToString();
    }
}
