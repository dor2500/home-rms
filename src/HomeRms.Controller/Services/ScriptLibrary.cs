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
    [JsonIgnore] public string ShellText => Shell == ScriptShell.Cmd ? "cmd" : "PowerShell";

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
        catch
        {
            // Corrupt file: start from the built-ins (the bad file is overwritten on the next save).
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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snippets.ToList(), Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Best effort; the in-memory library stays usable.
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
    private const string DoneMarker = "__HRMS_DONE__|";

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

            if (m.IsAppliance)
            {
                Ui(() => { run.State = ScriptRunState.Skipped; run.Summary = "Not a Windows endpoint"; });
                return;
            }

            var wrapped = BuildPowerShell(shell, body, title);

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
            if (ex is RpcException { StatusCode: StatusCode.PermissionDenied }) pool.MarkUnpaired(m.Host, m.Port);
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
    /// Wraps the snippet so multi-line bodies run as one unit, errors are captured and a completion
    /// marker (error flag + exit code) is printed last.
    /// </summary>
    public static string BuildPowerShell(ScriptShell shell, string body, string title)
    {
        var normalized = body.Replace("\r\n", "\n").TrimEnd().Replace("\n", "\r\n");
        // A line starting with '@ would end the here-string early: fall back to Base64 in that case.
        var hereStringSafe = !normalized.Split("\r\n").Any(l => l.StartsWith("'@", StringComparison.Ordinal));
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
