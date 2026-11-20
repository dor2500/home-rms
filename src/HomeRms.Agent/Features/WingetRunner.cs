using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Grpc.Core;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Agent.Features;

/// <summary>
/// Runs winget.exe directly (ProcessStartInfo.ArgumentList, never a shell string) and streams
/// per-package progress. Package IDs are validated by <see cref="WingetRules"/> before anything runs.
/// </summary>
public static partial class WingetRunner
{
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromMinutes(30);

    [GeneratedRegex(@"(?<!\d)(\d{1,3})\s?%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"([\d.,]+)\s*(B|KB|MB|GB)\s*/\s*([\d.,]+)\s*(B|KB|MB|GB)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    /// <summary>
    /// Mirrors the controller's PowerShell bootstrap: PATH lookup (Get-Command), the per-user
    /// app-execution alias, then the DesktopAppInstaller package folder under
    /// %ProgramFiles%\WindowsApps (the only option when running as SYSTEM).
    /// </summary>
    public static string? ResolveWingetPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "winget.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;

        try
        {
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                       System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            return Directory.GetDirectories(windowsApps, $"Microsoft.DesktopAppInstaller_*_{arch}__8wekyb3d8bbwe")
                .Select(d => (Dir: d, Version: ParsePackageVersion(Path.GetFileName(d))))
                .OrderByDescending(x => x.Version)
                .Select(x => Path.Combine(x.Dir, "winget.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null; // WindowsApps is not listable without elevation
        }
    }

    private static Version ParsePackageVersion(string folderName)
    {
        var parts = folderName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : new Version(0, 0);
    }

    public static async Task RunAsync(WingetRequest request, IServerStreamWriter<WingetProgress> writer,
        ServerCallContext context, string op, Action<string> log)
    {
        var ids = WingetRules.ValidateRequest(request);
        var winget = ResolveWingetPath()
            ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "winget.exe was not found on this computer (App Installer is missing or not registered)."));

        var ct = context.CancellationToken;
        log($"[winget] '{op}' requested {(request.UpgradeIfInstalled ? "upgrade/install" : "install")} of {string.Join(", ", ids)}" +
            $"{(request.HasVersion ? $" v{request.Version}" : "")} (silent={request.Silent}, machine={request.MachineScope})");

        foreach (var id in ids)
            await writer.WriteAsync(new WingetProgress { PackageId = id, Phase = WingetPhase.Queued, Percent = -1 }, ct);

        for (var i = 0; i < ids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var id = ids[i];
            var last = i == ids.Count - 1;

            int code;
            WingetPhase final;
            if (request.UpgradeIfInstalled)
            {
                code = await RunAttemptAsync(winget, "upgrade", id, request, writer, ct);
                if (unchecked((uint)code) == WingetRules.NoPackageFound)
                {
                    await writer.WriteAsync(new WingetProgress
                    {
                        PackageId = id, Phase = WingetPhase.Queued, Percent = -1,
                        LogLine = "Not installed yet — falling back to install."
                    }, ct);
                    code = await RunAttemptAsync(winget, "install", id, request, writer, ct);
                }
            }
            else
            {
                code = await RunAttemptAsync(winget, "install", id, request, writer, ct);
            }

            final = WingetRules.IsSuccess(code) ? WingetPhase.Succeeded
                : WingetRules.IsAlreadyInstalled(code) ? WingetPhase.Skipped
                : WingetPhase.Failed;
            var message = WingetRules.Describe(code);
            log($"[winget] '{op}' {id} -> {final} ({message})");

            await writer.WriteAsync(new WingetProgress
            {
                PackageId = id,
                Phase = final,
                Percent = final == WingetPhase.Failed ? -1 : 100,
                ExitCode = code,
                ExitMessage = message,
                Done = last,
            }, ct);
        }
    }

    private static async Task<int> RunAttemptAsync(string winget, string verb, string id, WingetRequest request,
        IServerStreamWriter<WingetProgress> writer, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = winget,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (winget.Contains(@"\WindowsApps\Microsoft.DesktopAppInstaller_", StringComparison.OrdinalIgnoreCase))
            psi.WorkingDirectory = Path.GetDirectoryName(winget)!;

        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add("--id");
        psi.ArgumentList.Add(id);
        psi.ArgumentList.Add("--exact");
        if (request.HasVersion)
        {
            psi.ArgumentList.Add("--version");
            psi.ArgumentList.Add(request.Version);
        }
        if (request.Silent) psi.ArgumentList.Add("--silent");
        if (request.MachineScope)
        {
            psi.ArgumentList.Add("--scope");
            psi.ArgumentList.Add("machine");
        }
        psi.ArgumentList.Add("--accept-package-agreements");
        psi.ArgumentList.Add("--accept-source-agreements");
        psi.ArgumentList.Add("--disable-interactivity");

        using var proc = new Process { StartInfo = psi };
        var lines = Channel.CreateUnbounded<string>();
        proc.Start();
        try { proc.StandardInput.Close(); } catch { }

        var pumps = Task.WhenAll(Pump(proc.StandardOutput, lines.Writer), Pump(proc.StandardError, lines.Writer))
            .ContinueWith(_ => lines.Writer.TryComplete(), TaskScheduler.Default);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerAttemptTimeout);

        var phase = WingetPhase.Queued;
        var lastPercent = int.MinValue;
        try
        {
            await foreach (var raw in lines.Reader.ReadAllAsync(timeout.Token))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line is "-" or "\\" or "|" or "/") continue;

                var newPhase = DetectPhase(line, phase);
                var percent = ParsePercent(line);
                var isProgressBar = percent >= 0 && IsProgressBar(line);

                if (isProgressBar)
                {
                    if (percent == lastPercent && newPhase == phase) continue;
                    lastPercent = percent;
                    phase = newPhase == WingetPhase.Queued ? WingetPhase.Downloading : newPhase;
                    await writer.WriteAsync(new WingetProgress { PackageId = id, Phase = phase, Percent = percent }, ct);
                    continue;
                }

                if (newPhase != phase) lastPercent = int.MinValue;
                phase = newPhase;
                await writer.WriteAsync(new WingetProgress
                {
                    PackageId = id,
                    Phase = phase,
                    Percent = percent,
                    LogLine = line.Length <= 2000 ? line : line[..2000],
                }, ct);
            }

            await proc.WaitForExitAsync(timeout.Token);
            await pumps;
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw; // client went away / cancelled the call
            return unchecked((int)WingetRules.CtrlSignalReceived); // our own per-attempt timeout
        }
    }

    /// <summary>Reads a stream character-wise, splitting on CR and LF so progress-bar redraws become lines.</summary>
    private static async Task Pump(StreamReader reader, ChannelWriter<string> output)
    {
        var buffer = new char[4096];
        var sb = new StringBuilder();
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (sb.Length > 0) { output.TryWrite(sb.ToString()); sb.Clear(); }
                    }
                    else if (sb.Length < 8192)
                    {
                        sb.Append(c);
                    }
                }
            }
        }
        catch { }
        if (sb.Length > 0) output.TryWrite(sb.ToString());
    }

    private static WingetPhase DetectPhase(string line, WingetPhase current)
    {
        if (line.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase)) return WingetPhase.Downloading;
        if (line.Contains("Starting package install", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Installing dependencies", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Installing", StringComparison.OrdinalIgnoreCase))
            return WingetPhase.Installing;
        return current;
    }

    private static int ParsePercent(string line)
    {
        var m = PercentRegex().Match(line);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p) && p <= 100) return p;

        var s = SizeRegex().Match(line);
        if (s.Success &&
            TryBytes(s.Groups[1].Value, s.Groups[2].Value, out var done) &&
            TryBytes(s.Groups[3].Value, s.Groups[4].Value, out var total) && total > 0)
            return (int)Math.Clamp(done * 100 / total, 0, 100);
        return -1;
    }

    private static bool TryBytes(string number, string unit, out double bytes)
    {
        bytes = 0;
        if (!double.TryParse(number.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        bytes = unit.ToUpperInvariant() switch
        {
            "KB" => v * 1024,
            "MB" => v * 1024 * 1024,
            "GB" => v * 1024 * 1024 * 1024,
            _ => v,
        };
        return true;
    }

    /// <summary>winget progress bars are drawn with block characters followed by "x MB / y MB" or "n%".</summary>
    private static bool IsProgressBar(string line) =>
        line.Contains('█') || line.Contains('▒') || SizeRegex().IsMatch(line) ||
        PercentRegex().Match(line) is { Success: true } m && m.Index + m.Length >= line.Length - 1 && line.Length < 120;
}
