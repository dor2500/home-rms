using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Grpc.Core;
using HomeRms.Agent.Linux.Core;
using HomeRms.Agent.Linux.Features;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Linux.Server;

/// <summary>Linux implementation of the consent-gated Home RMS gRPC service.</summary>
public sealed class RemoteManagementService(ConsentCoordinator consent, ActivityLog log)
    : RemoteManagement.RemoteManagementBase
{
    private const long MaxDeployBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".deb", ".rpm", ".sh", ".appimage"
    };

    private static string OperatorOf(ServerCallContext ctx)
    {
        var cert = ctx.GetHttpContext().Connection.ClientCertificate;
        return cert?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "unknown";
    }

    public override Task<AgentInfo> GetAgentInfo(Empty request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        log.Write($"[info] '{op}' connected and requested agent info");
        using var cap = new LinuxScreenCapturer();
        var (w, h) = cap.PrimaryBounds;
        return Task.FromResult(new AgentInfo
        {
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            AgentVersion = typeof(RemoteManagementService).Assembly.GetName().Version?.ToString() ?? "0.6.0",
            LoggedInUser = Environment.UserName,
            PrimaryScreenWidth = w,
            PrimaryScreenHeight = h
        });
    }

    public override Task<InventorySnapshot> GetInventory(Empty request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        log.Write($"[inventory] '{op}' pulled the software/hardware inventory");
        return Task.FromResult(LinuxInventoryCollector.Collect());
    }

    public override async Task StreamScreen(
        ScreenRequest request, IServerStreamWriter<ScreenFrame> responseStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        if (!consent.RequestGrant(op, "view your screen"))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Screen sharing was declined on the Linux device."));

        var fps = Math.Clamp(request.Fps <= 0 ? 15 : request.Fps, 1, 60);
        var intervalMs = 1000.0 / fps;
        var quality = request.Quality <= 0 ? 65 : request.Quality;
        var maxWidth = request.MaxWidth;

        log.Write($"[screen] '{op}' started viewing the screen");
        using var cap = new LinuxScreenCapturer();
        var sw = new Stopwatch();
        long seq = 0;

        // X11: one continuous ffmpeg capture (much faster than a screenshot tool per frame)
        var (sw0, sh0) = cap.PrimaryBounds;
        using var live = FfmpegScreenStream.TryStart(fps, quality, maxWidth, sw0, sh0);
        if (live is not null)
        {
            log.Write($"[screen] continuous X11 capture active ({live.Width}×{live.Height} @ {fps} fps)");
            await StreamFromFfmpegAsync(op, live, responseStream, context);
            if (!live.HasExited || context.CancellationToken.IsCancellationRequested || !consent.HasActiveGrant(op))
            {
                log.Write($"[screen] '{op}' stopped viewing the screen");
                return;
            }
            log.Write("[screen] ffmpeg capture stopped; falling back to screenshots");
        }

        try
        {
            byte[]? lastSent = null;
            while (!context.CancellationToken.IsCancellationRequested)
            {
                if (!consent.HasActiveGrant(op)) break;

                sw.Restart();
                var (jpeg, w, h) = cap.CaptureJpeg(quality, maxWidth);
                // Identical screenshot = nothing changed: don't spend bandwidth on it
                if (lastSent is null || !jpeg.AsSpan().SequenceEqual(lastSent))
                {
                    lastSent = jpeg;
                    await responseStream.WriteAsync(new ScreenFrame
                    {
                        Jpeg = Google.Protobuf.ByteString.CopyFrom(jpeg),
                        Width = w,
                        Height = h,
                        Sequence = ++seq,
                        CaptureUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }

                var remaining = intervalMs - sw.Elapsed.TotalMilliseconds;
                if (remaining > 1)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(remaining), context.CancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            log.Write($"[screen] '{op}' stopped viewing the screen");
        }
    }

    /// <summary>Sends each new ffmpeg frame as soon as it exists; identical frames (still screen) are skipped.</summary>
    private async Task StreamFromFfmpegAsync(string op, FfmpegScreenStream live,
        IServerStreamWriter<ScreenFrame> responseStream, ServerCallContext context)
    {
        long sentSeq = 0, seq = 0;
        byte[]? lastSent = null;
        while (!context.CancellationToken.IsCancellationRequested && consent.HasActiveGrant(op) && !live.HasExited)
        {
            var (jpeg, frameSeq) = live.Latest;
            if (jpeg is null || frameSeq == sentSeq)
            {
                try { await Task.Delay(4, context.CancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            sentSeq = frameSeq;
            if (lastSent is not null && jpeg.AsSpan().SequenceEqual(lastSent))
                continue;
            lastSent = jpeg;

            await responseStream.WriteAsync(new ScreenFrame
            {
                Jpeg = Google.Protobuf.ByteString.CopyFrom(jpeg),
                Width = live.Width,
                Height = live.Height,
                Sequence = ++seq,
                CaptureUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
    }

    public override async Task<InputAck> SendInput(
        IAsyncStreamReader<InputEvent> requestStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        if (!consent.HasActiveGrant(op))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "No active control grant."));

        using var cap = new LinuxScreenCapturer();
        var (w, h) = cap.PrimaryBounds;
        long applied = 0;

        await foreach (var ev in requestStream.ReadAllAsync(context.CancellationToken))
        {
            if (!consent.HasActiveGrant(op)) break;
            LinuxInputInjector.Apply(ev, w, h);
            applied++;
        }
        log.Write($"[input] applied {applied} input events from '{op}'");
        return new InputAck { EventsApplied = applied };
    }

    public override async Task Shell(
        IAsyncStreamReader<ShellInput> requestStream, IServerStreamWriter<ShellOutput> responseStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        log.Write($"[shell] '{op}' opened a remote bash shell");

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-s",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var writeLock = new SemaphoreSlim(1, 1);
        async Task Pump(StreamReader reader, bool isError)
        {
            var buffer = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buffer)) > 0)
            {
                await writeLock.WaitAsync();
                try { await responseStream.WriteAsync(new ShellOutput { Data = new string(buffer, 0, n), IsError = isError }); }
                catch { break; }
                finally { writeLock.Release(); }
            }
        }

        proc.Start();
        var outTask = Pump(proc.StandardOutput, false);
        var errTask = Pump(proc.StandardError, true);

        try
        {
            await foreach (var input in requestStream.ReadAllAsync(context.CancellationToken))
            {
                if (input.Close) break;
                if (!string.IsNullOrEmpty(input.Data))
                {
                    log.Write($"[shell] '{op}' > {input.Data.Trim()}");
                    await proc.StandardInput.WriteLineAsync(input.Data);
                    await proc.StandardInput.FlushAsync();
                }
            }
        }
        catch (OperationCanceledException) { }

        try { proc.StandardInput.Close(); } catch { }
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        await Task.WhenAll(outTask, errTask);
        log.Write($"[shell] '{op}' closed the remote shell");
    }

    public override async Task<DeployResult> DeployPackage(
        IAsyncStreamReader<PackageChunk> requestStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        var deployDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HomeRMS", "DeployStaging");
        Directory.CreateDirectory(deployDir);

        string fileName = "package.sh";
        string customArgs = "";
        int timeoutSec = 300;
        var tempPath = Path.Combine(deployDir, $"pkg_{Guid.NewGuid():N}.tmp");
        long totalBytes = 0;

        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await foreach (var chunk in requestStream.ReadAllAsync(context.CancellationToken))
                {
                    if (!string.IsNullOrWhiteSpace(chunk.FileName))
                    {
                        var rawName = Path.GetFileName(chunk.FileName);
                        fileName = Regex.Replace(rawName, @"[^a-zA-Z0-9._\-]", "_");
                    }
                    if (!string.IsNullOrWhiteSpace(chunk.Arguments))
                        customArgs = chunk.Arguments.Trim();
                    if (chunk.TimeoutSeconds > 0)
                        timeoutSec = Math.Clamp(chunk.TimeoutSeconds, 15, 1800);

                    if (chunk.Data.Length > 0)
                    {
                        totalBytes += chunk.Data.Length;
                        if (totalBytes > MaxDeployBytes)
                            throw new InvalidOperationException("Package exceeds 512 MB limit.");
                        chunk.Data.WriteTo(fs);
                    }
                }
            }

            var ext = Path.GetExtension(fileName);
            if (!AllowedExtensions.Contains(ext))
            {
                return new DeployResult
                {
                    Success = false,
                    ExitCode = -1,
                    CommandLine = fileName,
                    Output = $"Blocked unsupported Linux package extension '{ext}'. Allowed: .deb, .rpm, .sh, .AppImage."
                };
            }

            var finalPath = Path.Combine(deployDir, $"{Guid.NewGuid():N}_{fileName}");
            File.Move(tempPath, finalPath, overwrite: true);
            tempPath = finalPath;

            var (cmd, args, display) = ext.ToLowerInvariant() switch
            {
                ".deb" => ("/bin/bash", $"-c \"DEBIAN_FRONTEND=noninteractive dpkg -i '{finalPath}' || DEBIAN_FRONTEND=noninteractive apt-get install -f -y\"", $"dpkg -i {fileName}"),
                ".rpm" => ("/bin/bash", $"-c \"rpm -Uvh '{finalPath}' || dnf install -y '{finalPath}'\"", $"rpm -Uvh {fileName}"),
                ".appimage" => ("/bin/bash", $"-c \"chmod +x '{finalPath}' && '{finalPath}' {customArgs}\"", $"{fileName}"),
                _ => ("/bin/bash", $"\"{finalPath}\" {(string.Equals(customArgs, "AUTO", StringComparison.OrdinalIgnoreCase) ? "" : customArgs)}", $"bash {fileName}")
            };

            log.Write($"[deploy] '{op}' deploying '{fileName}' ({totalBytes / 1024} KB) -> {display}");
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            await proc.WaitForExitAsync(cts.Token);

            var combined = string.Join("\n", new[] { (await outTask).Trim(), (await errTask).Trim() }.Where(s => !string.IsNullOrEmpty(s)));
            var ok = proc.ExitCode == 0;
            return new DeployResult { Success = ok, ExitCode = proc.ExitCode, CommandLine = display, Output = combined };
        }
        catch (Exception ex)
        {
            return new DeployResult { Success = false, ExitCode = -1, CommandLine = fileName, Output = ex.Message };
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    public override Task<KillProcessResult> KillProcess(KillProcessRequest request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        try
        {
            using var p = Process.GetProcessById(request.Pid);
            var name = p.ProcessName;
            p.Kill(entireProcessTree: true);
            log.Write($"[process] '{op}' terminated process '{name}' (PID {request.Pid})");
            return Task.FromResult(new KillProcessResult { Success = true, Message = $"Terminated '{name}' (PID {request.Pid})." });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new KillProcessResult { Success = false, Message = ex.Message });
        }
    }

    public override Task<ClipboardData> GetClipboard(Empty request, ServerCallContext context)
    {
        var text = ReadSystemClipboard() ?? "";
        log.Write($"[clipboard] '{OperatorOf(context)}' copied clipboard text from this computer ({text.Length} chars)");
        return Task.FromResult(new ClipboardData { Text = text });
    }

    public override Task<ClipboardAck> SetClipboard(ClipboardData request, ServerCallContext context)
    {
        var text = request.Text ?? "";
        if (text.Length > 1024 * 1024) text = text[..(1024 * 1024)];
        var ok = TryWriteClipboard("wl-copy", "", text) ||
                 TryWriteClipboard("xclip", "-selection clipboard", text) ||
                 TryWriteClipboard("xsel", "-ib", text);
        if (ok) Volatile.Write(ref _lastClipboardFromOperator, text);
        log.Write($"[clipboard] '{OperatorOf(context)}' pasted clipboard text to this computer ({text.Length} chars)");
        return Task.FromResult(new ClipboardAck { Success = ok });
    }

    // Text the operator last put on the clipboard — not echoed back by WatchClipboard
    private static string? _lastClipboardFromOperator;

    private static string? ReadSystemClipboard() =>
        TryReadClipboard("wl-paste", "-n")
        ?? TryReadClipboard("xclip", "-o -selection clipboard")
        ?? TryReadClipboard("xsel", "-ob");

    /// <summary>
    /// Live clipboard: every time text is copied on this computer (Ctrl+C, right-click → Copy, any app)
    /// it is sent to the operator. Only while the operator holds an active grant; each transfer is logged.
    /// </summary>
    public override async Task WatchClipboard(Empty request, IServerStreamWriter<ClipboardData> responseStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        if (!consent.HasActiveGrant(op))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "No active grant. Start screen sharing first."));

        log.Write($"[clipboard] live copy/paste sync started for '{op}'");
        var last = ReadSystemClipboard(); // what is there now is not "new"
        try
        {
            while (!context.CancellationToken.IsCancellationRequested && consent.HasActiveGrant(op))
            {
                await Task.Delay(400, context.CancellationToken);
                var text = ReadSystemClipboard();
                if (string.IsNullOrEmpty(text) || text == last) continue;
                last = text;
                if (text == Volatile.Read(ref _lastClipboardFromOperator)) continue;
                if (text.Length > 1024 * 1024) text = text[..(1024 * 1024)];

                await responseStream.WriteAsync(new ClipboardData { Text = text });
                log.Write($"[clipboard] text copied on this computer was sent to '{op}' ({text.Length} chars)");
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            log.Write($"[clipboard] live copy/paste sync ended for '{op}'");
        }
    }

    public override async Task<SystemOpResult> SystemOp(SystemOpRequest request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        log.Write($"[system] '{op}' requested {request.Area}/{request.Action}");
        var res = await LinuxSystemOps.RunAsync(request, context.CancellationToken);
        log.Write($"[system] {request.Area}/{request.Action} -> {(res.Success ? "OK" : "FAILED")}");
        return res;
    }

    private static string? TryReadClipboard(string tool, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return null;
            var s = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1000);
            return p.ExitCode == 0 ? s : null;
        }
        catch { return null; }
    }

    private static bool TryWriteClipboard(string tool, string args, string text)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return false;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit(1000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
