using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Grpc.Core;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Server;

namespace HomeRms.Agent.Linux.Features;

/// <summary>
/// "Update All" on Linux: detects the package manager (apt-get, dnf, yum, zypper, pacman — first found
/// wins), runs its non-interactive refresh + upgrade commands (argument lists, no shell) and streams the
/// output through <see cref="LinuxUpgradeTracker"/>. Each command runs in its own process group
/// (setsid) so cancelling the call can signal the whole group.
/// </summary>
public static class LinuxPackageUpgrader
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromHours(3);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string[] FallbackDirs = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    private const int SIGTERM = 15;
    private const int SIGKILL = 9;

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int SysKill(int pid, int sig);

    /// <summary>Absolute path of <paramref name="binary"/> on PATH (plus the standard sbin/bin dirs), or null.</summary>
    public static string? FindBinary(string binary)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Concat(FallbackDirs);
        foreach (var dir in dirs.Distinct())
        {
            try
            {
                var candidate = Path.Combine(dir, binary);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public static async Task RunAsync(UpgradeAllRequest request, IServerStreamWriter<WingetProgress> writer,
        ServerCallContext context, string op, Action<string> log)
    {
        var ct = context.CancellationToken;
        var manager = LinuxUpgradeParser.Detect(b => FindBinary(b) is not null);
        if (manager == LinuxPackageManager.None)
        {
            log($"[update-all] '{op}' requested Update All — no supported package manager found");
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "No supported package manager was found (apt-get, dnf, yum, zypper or pacman)."));
        }
        if (!Environment.IsPrivilegedProcess)
        {
            log($"[update-all] '{op}' requested Update All — refused, the agent is not running as root");
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Update All needs root: the Home RMS agent runs as '{Environment.UserName}'. Run the agent as root (its systemd service does) to upgrade packages."));
        }
        if (!await Gate.WaitAsync(0, ct))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "An Update All run is already in progress on this computer."));

        try
        {
            var bin = LinuxUpgradeParser.BinaryOf(manager);
            log($"[update-all] '{op}' started Update All with {bin}");
            var tracker = new LinuxUpgradeTracker(manager);
            var setsid = FindBinary("setsid");

            foreach (var step in LinuxUpgradeParser.Plan(manager))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var m in tracker.BeginStep(step)) await writer.WriteAsync(m, ct);
                var exe = FindBinary(step.FileName) ?? step.FileName;
                var code = await RunStepAsync(exe, step, setsid, ct, line => WriteAll(writer, tracker.Feed(line), ct));
                if (code != 0)
                {
                    var why = $"'{step.Display}' failed with exit code {code}" +
                              (code == 100 && manager == LinuxPackageManager.Apt ? " (another package manager may be running, or a repository is unreachable)" : "");
                    var final = tracker.Complete(code, why);
                    await WriteAll(writer, final, ct);
                    log($"[update-all] Update All for '{op}' -> FAILED: {why}");
                    return;
                }
            }

            var done = tracker.Complete(0);
            await WriteAll(writer, done, ct);
            log($"[update-all] Update All for '{op}' -> {done[^1].Phase}: {done[^1].ExitMessage}");
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or RpcException)
        {
            log($"[update-all] Update All for '{op}' cancelled — process group terminated");
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task WriteAll(IServerStreamWriter<WingetProgress> writer, List<WingetProgress> messages, CancellationToken ct)
    {
        foreach (var m in messages) await writer.WriteAsync(m, ct);
    }

    private static async Task<int> RunStepAsync(string exe, LinuxUpgradeStep step, string? setsid, CancellationToken ct, Func<string, Task> onLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = setsid ?? exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // setsid execs the command in place (our child is never a group leader), so the pid is the group id.
        if (setsid is not null) psi.ArgumentList.Add(exe);
        foreach (var a in step.Arguments) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";   // stable English output for the parser
        psi.Environment["LANG"] = "C";
        foreach (var (k, v) in step.Environment) psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        var lines = Channel.CreateUnbounded<string>();
        proc.Start();
        try { proc.StandardInput.Close(); } catch { }

        var pumps = Task.WhenAll(Pump(proc.StandardOutput, lines.Writer), Pump(proc.StandardError, lines.Writer))
            .ContinueWith(_ => lines.Writer.TryComplete(), TaskScheduler.Default);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(StepTimeout);
        try
        {
            await foreach (var line in lines.Reader.ReadAllAsync(limit.Token))
                await onLine(line);
            await proc.WaitForExitAsync(limit.Token);
            await pumps;
            return proc.ExitCode;
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is RpcException && ct.IsCancellationRequested)
        {
            KillGroup(proc, setsid is not null);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return 124; // timeout(1) convention
        }
        catch
        {
            KillGroup(proc, setsid is not null);
            throw;
        }
    }

    /// <summary>SIGTERM to the process group, SIGKILL after 5 s, then the managed tree kill as a fallback.</summary>
    private static void KillGroup(Process proc, bool ownGroup)
    {
        try
        {
            if (proc.HasExited) return;
            var pid = proc.Id;
            if (ownGroup)
            {
                SysKill(-pid, SIGTERM);
                if (!proc.WaitForExit(5000)) SysKill(-pid, SIGKILL);
            }
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            proc.WaitForExit(5000);
        }
        catch { }
    }

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
