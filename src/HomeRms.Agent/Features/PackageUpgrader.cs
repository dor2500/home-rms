using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Grpc.Core;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Agent.Features;

/// <summary>
/// "Update All": lists the available upgrades (<c>winget upgrade</c>) so every package gets a Queued row,
/// then runs <c>winget upgrade --all</c> unattended and streams per-package progress parsed by
/// <see cref="WingetUpgradeTracker"/>. winget.exe is resolved exactly like <see cref="WingetRunner"/>
/// and run with an argument list (no shell). Cancelling the call kills the whole winget process tree.
/// </summary>
public static class PackageUpgrader
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UpgradeTimeout = TimeSpan.FromHours(4);

    /// <summary>One run at a time: parallel winget upgrades fight over the same installers.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task RunAsync(UpgradeAllRequest request, IServerStreamWriter<WingetProgress> writer,
        ServerCallContext context, string op, Action<string> log)
    {
        var ct = context.CancellationToken;
        var winget = WingetRunner.ResolveWingetPath();
        if (winget is null)
        {
            log($"[winget] '{op}' requested Update All — winget.exe was not found");
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "winget.exe was not found on this computer (App Installer is missing or not registered)."));
        }
        if (!await Gate.WaitAsync(0, ct))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "An Update All run is already in progress on this computer."));

        try
        {
            log($"[winget] '{op}' started Update All (include unknown={request.IncludeUnknown}, silent={request.Silent})");

            // 1) list the available upgrades (read-only)
            List<string> listArgs = ["upgrade", "--accept-source-agreements", "--disable-interactivity"];
            if (request.IncludeUnknown) listArgs.Add("--include-unknown");
            var listing = new StringBuilder();
            var listCode = await RunStreamingAsync(winget, listArgs, ListTimeout, ct,
                onStarted: () => writer.WriteAsync(new WingetProgress
                {
                    PackageId = "", Phase = WingetPhase.Queued, Percent = -1,
                    LogLine = "Checking for available upgrades (winget upgrade)...",
                }, ct),
                onLine: async line =>
                {
                    listing.Append(line).Append('\n');
                    var t = line.Trim();
                    if (t.Length == 0 || WingetUpgradeParser.IsSpinner(t) ||
                        WingetUpgradeParser.ParsePercent(t) >= 0 && WingetUpgradeParser.IsProgressBar(t)) return;
                    await writer.WriteAsync(new WingetProgress { PackageId = "", Phase = WingetPhase.Queued, Percent = -1, LogLine = Clip(t) }, ct);
                });

            var candidates = WingetUpgradeParser.ParseUpgradeList(listing.ToString());
            var tracker = new WingetUpgradeTracker(candidates);
            foreach (var row in tracker.QueuedRows()) await writer.WriteAsync(row, ct);
            log($"[winget] Update All: {candidates.Count} upgrade(s) available" +
                (candidates.Count == 0 ? "" : $": {string.Join(", ", candidates.Take(20).Select(c => c.Key))}{(candidates.Count > 20 ? ", ..." : "")}"));

            // 2) nothing listed and winget agrees -> done without running --all
            var listU = unchecked((uint)listCode);
            if (candidates.Count == 0 && (listCode == 0 || listU is WingetRules.UpdateNotApplicable or WingetRules.NoPackageFound))
            {
                await WriteAllAsync(writer, tracker.Complete(0), ct);
                log($"[winget] Update All for '{op}' -> everything is already up to date");
                return;
            }

            // 3) upgrade everything
            List<string> args = ["upgrade", "--all"];
            if (request.Silent) args.Add("--silent");
            args.AddRange(["--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"]);
            if (request.IncludeUnknown) args.Add("--include-unknown");

            var code = await RunStreamingAsync(winget, args, UpgradeTimeout, ct,
                onStarted: () => writer.WriteAsync(new WingetProgress
                {
                    PackageId = "", Phase = WingetPhase.Installing, Percent = -1,
                    LogLine = $"Running: winget {string.Join(' ', args)}",
                }, ct),
                onLine: line => WriteAllAsync(writer, tracker.Feed(line), ct));

            var final = tracker.Complete(code);
            await WriteAllAsync(writer, final, ct);
            log($"[winget] Update All for '{op}' -> {final[^1].Phase}: {final[^1].ExitMessage}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log($"[winget] Update All for '{op}' cancelled — winget process tree killed");
            throw;
        }
        catch (RpcException) when (ct.IsCancellationRequested)
        {
            log($"[winget] Update All for '{op}' cancelled — winget process tree killed");
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task WriteAllAsync(IServerStreamWriter<WingetProgress> writer, List<WingetProgress> messages, CancellationToken ct)
    {
        foreach (var m in messages) await writer.WriteAsync(m, ct);
    }

    private static string Clip(string s) => s.Length <= 2000 ? s : s[..2000];

    /// <summary>
    /// Runs winget with <paramref name="args"/>, forwarding every CR/LF-separated output piece to
    /// <paramref name="onLine"/>. Cancellation kills the process tree and rethrows; the timeout kills it and
    /// returns the winget "cancelled" exit code.
    /// </summary>
    private static async Task<int> RunStreamingAsync(string winget, IEnumerable<string> args, TimeSpan timeout,
        CancellationToken ct, Func<Task> onStarted, Func<string, Task> onLine)
    {
        ct.ThrowIfCancellationRequested();
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
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var lines = Channel.CreateUnbounded<string>();
        proc.Start();
        try { proc.StandardInput.Close(); } catch { }

        var pumps = Task.WhenAll(Pump(proc.StandardOutput, lines.Writer), Pump(proc.StandardError, lines.Writer))
            .ContinueWith(_ => lines.Writer.TryComplete(), TaskScheduler.Default);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await onStarted();
            await foreach (var line in lines.Reader.ReadAllAsync(limit.Token))
                await onLine(line);
            await proc.WaitForExitAsync(limit.Token);
            await pumps;
            return proc.ExitCode;
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is RpcException && ct.IsCancellationRequested)
        {
            KillTree(proc);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return unchecked((int)WingetRules.CtrlSignalReceived); // our own timeout
        }
        catch
        {
            KillTree(proc);
            throw;
        }
    }

    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
        }
        catch { }
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
}
