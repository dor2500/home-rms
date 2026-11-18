using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Forms;
using Grpc.Core;
using HomeRms.Agent.Core;
using HomeRms.Agent.Features;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

namespace HomeRms.Agent.Server;

/// <summary>Server-side implementation of the consent-gated remote management RPCs.</summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteManagementService(ConsentCoordinator consent, ActivityLog log)
    : RemoteManagement.RemoteManagementBase
{
    private static string OperatorOf(ServerCallContext ctx)
    {
        var cert = ctx.GetHttpContext().Connection.ClientCertificate;
        return cert?.GetNameInfo(X509NameType.SimpleName, forIssuer: false) ?? "unknown";
    }

    public override Task<AgentInfo> GetAgentInfo(Empty request, ServerCallContext context)
    {
        var cert = context.GetHttpContext().Connection.ClientCertificate;
        var op = OperatorOf(context);

        if (cert is not null && !TrustedControllerStore.HasPinned()
            && cert.Subject.Contains("CN=HomeRMS-Controller-", StringComparison.Ordinal))
        {
            var cfg = TwoFactorStore.LoadAgent();
            if (cfg.RequireOnStartupOrPair)
            {
                log.Write($"[security] '{op}' requires initial 2FA pairing");
                throw new RpcException(new Status(StatusCode.Unauthenticated, "2FA_REQUIRED"));
            }

            var fp = ControllerIdentity.GetSha256Fingerprint(cert);
            if (!consent.RequestGrant(op, $"pair & permanently pin this Controller\n({fp[..23]}...)"))
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Controller pairing was declined."));
            TrustedControllerStore.AddPinned(fp, op);
            log.Write($"[security] pinned Controller '{op}' ({fp[..23]}...)");
        }

        log.Write($"[info] '{op}' connected and requested agent info");
        using var cap = new ScreenCapturer();
        var (w, h) = cap.PrimaryBounds;
        return Task.FromResult(new AgentInfo
        {
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.VersionString,
            AgentVersion = typeof(RemoteManagementService).Assembly.GetName().Version?.ToString() ?? "1.0",
            LoggedInUser = Environment.UserName,
            PrimaryScreenWidth = w,
            PrimaryScreenHeight = h,
            MonitorCount = Math.Max(1, Screen.AllScreens.Length)
        });
    }

    public override Task<InventorySnapshot> GetInventory(Empty request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        log.Write($"[inventory] '{op}' pulled the software/hardware inventory");
        return Task.FromResult(InventoryCollector.Collect());
    }

    public override async Task StreamScreen(
        ScreenRequest request, IServerStreamWriter<ScreenFrame> responseStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        if (!consent.RequestGrant(op, "view your screen"))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Screen sharing was declined on the device."));

        var fps = Math.Clamp(request.Fps <= 0 ? 15 : request.Fps, 1, 60);
        var intervalMs = 1000.0 / fps;
        var quality = request.Quality <= 0 ? 88 : request.Quality;
        var maxWidth = request.MaxWidth;

        using var _ = consent.BeginViewing(op);
        using var cap = new ScreenCapturer { MonitorIndex = Math.Max(0, request.MonitorIndex) };
        var sw = new Stopwatch();
        long seq = 0;

        // Tile-capable controller at native resolution on primary monitor: GPU capture + dirty tiles
        if (request.SupportsTiles && cap.MonitorIndex == 0 && (maxWidth <= 0 || maxWidth >= cap.PrimaryBounds.Width))
        {
            using var tiles = new TileStreamer(log.Write);
            if (tiles.Available)
            {
                await StreamTilesAsync(op, tiles, cap, quality, intervalMs, responseStream, context);
                return;
            }
        }

        while (!context.CancellationToken.IsCancellationRequested)
        {
            if (!consent.HasActiveGrant(op))
            {
                log.Write($"[screen] grant for '{op}' expired/revoked — stopping stream");
                break;
            }

            sw.Restart();
            byte[]? jpeg;
            int w, h;
            try { (jpeg, w, h) = cap.CaptureNext(quality, maxWidth); }
            catch (Exception ex) { log.Write($"[screen] capture error: {ex.Message}"); break; }

            // null = screen unchanged since the last frame: send nothing
            if (jpeg is not null)
            {
                await responseStream.WriteAsync(new ScreenFrame
                {
                    Jpeg = Google.Protobuf.ByteString.CopyFrom(jpeg),
                    Width = w,
                    Height = h,
                    Sequence = ++seq
                });
            }

            var remainingMs = intervalMs - sw.Elapsed.TotalMilliseconds;
            if (remainingMs > 1)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), context.CancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Event-driven loop: waits on the GPU for the next change (instead of polling on a timer),
    /// so an update leaves the agent within milliseconds of appearing on screen.
    /// Falls back to full GDI frames while GPU capture is unavailable (UAC prompt, lock screen).
    /// </summary>
    private async Task StreamTilesAsync(string op, TileStreamer tiles, ScreenCapturer cap, int quality, double intervalMs,
        IServerStreamWriter<ScreenFrame> responseStream, ServerCallContext context)
    {
        long seq = 0;
        var sinceLastSend = Stopwatch.StartNew();
        var fallbackFrames = 0;

        while (!context.CancellationToken.IsCancellationRequested)
        {
            if (!consent.HasActiveGrant(op))
            {
                log.Write($"[screen] grant for '{op}' expired/revoked — stopping stream");
                break;
            }

            // Frame-rate cap: wait *before* capturing so what we send is always the freshest image
            var waitMs = intervalMs - sinceLastSend.Elapsed.TotalMilliseconds;
            if (waitMs > 1)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(waitMs), context.CancellationToken); }
                catch (OperationCanceledException) { break; }
            }

            ScreenFrame? frame;
            try
            {
                if (tiles.Available)
                {
                    fallbackFrames = 0;
                    frame = tiles.Next(quality, waitMs: 16);
                }
                else
                {
                    // GPU capture is down: keep the picture alive with a few GDI frames per second
                    var (jpeg, w, h) = cap.CaptureJpeg(quality, 0);
                    frame = new ScreenFrame { Jpeg = Google.Protobuf.ByteString.CopyFrom(jpeg), Width = w, Height = h };
                    fallbackFrames++;
                    await Task.Delay(fallbackFrames < 3 ? 100 : 400, context.CancellationToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.Write($"[screen] capture error: {ex.Message}"); break; }

            if (frame is null) continue;

            frame.Sequence = ++seq;
            await responseStream.WriteAsync(frame);
            sinceLastSend.Restart();
        }
    }

    public override async Task<InputAck> SendInput(
        IAsyncStreamReader<InputEvent> requestStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        if (!consent.HasActiveGrant(op))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "No active control grant. Start screen sharing first."));

        long applied = 0;
        await foreach (var ev in requestStream.ReadAllAsync(context.CancellationToken))
        {
            if (!consent.HasActiveGrant(op)) break;
            InputInjector.Apply(ev);
            applied++;
        }
        log.Write($"[input] applied {applied} input events from '{op}'");
        return new InputAck { EventsApplied = applied };
    }

    public override async Task Shell(
        IAsyncStreamReader<ShellInput> requestStream, IServerStreamWriter<ShellOutput> responseStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        log.Write($"[shell] '{op}' opened a remote shell");

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command -",
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
        catch (OperationCanceledException) { /* client disconnected */ }

        try { proc.StandardInput.Close(); } catch { }
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        await Task.WhenAll(outTask, errTask);
        log.Write($"[shell] '{op}' closed the remote shell");
    }

    private const long MaxDeployBytes = 512L * 1024 * 1024; // 512 MB limit against disk exhaustion
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msp", ".ps1", ".cmd", ".bat"
    };

    public override async Task<DeployResult> DeployPackage(
        IAsyncStreamReader<PackageChunk> requestStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        // Use per-user LocalApplicationData instead of shared %TEMP% to prevent multi-user symlink/TOCTOU attacks
        var deployDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HomeRMS", "DeployStaging");
        Directory.CreateDirectory(deployDir);

        string fileName = "installer.exe";
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
                        {
                            throw new InvalidOperationException($"Package exceeds maximum allowed size ({MaxDeployBytes / (1024 * 1024)} MB).");
                        }
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
                    Output = $"Blocked unsupported file extension '{ext}'. Allowed: .exe, .msi, .msp, .ps1, .cmd, .bat."
                };
            }

            if (totalBytes == 0)
            {
                return new DeployResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = "Uploaded package was empty (0 bytes)."
                };
            }

            var finalPath = Path.Combine(deployDir, $"{Guid.NewGuid():N}_{fileName}");
            File.Move(tempPath, finalPath, overwrite: true);
            tempPath = finalPath;

            var (exeFile, finalArgs, displayCmd) = BuildSilentCommand(finalPath, fileName, customArgs);
            log.Write($"[deploy] '{op}' deploying '{fileName}' ({totalBytes / 1024} KB) -> {displayCmd}");

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exeFile,
                    Arguments = finalArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = deployDir
                }
            };

            proc.Start();
            var stdOutTask = proc.StandardOutput.ReadToEndAsync();
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                log.Write($"[deploy] '{fileName}' timed out after {timeoutSec}s");
                return new DeployResult
                {
                    Success = false,
                    ExitCode = -2,
                    CommandLine = displayCmd,
                    Output = $"Installer timed out after {timeoutSec} seconds."
                };
            }

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            var code = proc.ExitCode;
            // Exit codes 0, 1641 (reboot initiated), and 3010 (reboot required) are standard Windows installer success codes
            var ok = code is 0 or 1641 or 3010;
            var combined = string.Join("\r\n", new[] { stdOut.Trim(), stdErr.Trim() }.Where(s => !string.IsNullOrEmpty(s)));
            if (string.IsNullOrWhiteSpace(combined))
                combined = ok ? $"Silent installation completed successfully (Exit Code {code})." : $"Installer exited with code {code}.";

            log.Write($"[deploy] '{fileName}' finished (exit={code}, ok={ok})");
            return new DeployResult
            {
                Success = ok,
                ExitCode = code,
                CommandLine = displayCmd,
                Output = combined
            };
        }
        catch (Exception ex)
        {
            log.Write($"[deploy] error installing '{fileName}': {ex.Message}");
            return new DeployResult
            {
                Success = false,
                ExitCode = -1,
                CommandLine = fileName,
                Output = ex.Message
            };
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static (string Exe, string Args, string Display) BuildSilentCommand(string fullPath, string fileName, string customArgs)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var auto = string.IsNullOrWhiteSpace(customArgs) || string.Equals(customArgs, "AUTO", StringComparison.OrdinalIgnoreCase);

        if (ext == ".msi")
        {
            var msiFlags = auto ? "/qn /norestart" : customArgs;
            var args = $"/i \"{fullPath}\" {msiFlags}";
            return ("msiexec.exe", args, $"msiexec.exe /i \"{fileName}\" {msiFlags}");
        }

        if (ext == ".msp")
        {
            var mspFlags = auto ? "/qn /norestart" : customArgs;
            var args = $"/p \"{fullPath}\" {mspFlags}";
            return ("msiexec.exe", args, $"msiexec.exe /p \"{fileName}\" {mspFlags}");
        }

        if (ext == ".ps1")
        {
            var extra = auto ? "" : $" {customArgs}";
            var psArgs = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{fullPath}\"{extra}";
            return ("powershell.exe", psArgs, $"powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{fileName}\"{extra}");
        }

        if (ext is ".cmd" or ".bat")
        {
            var extra = auto ? "" : $" {customArgs}";
            var cmdArgs = $"/d /s /c \"\"{fullPath}\"{extra}\"";
            return ("cmd.exe", cmdArgs, $"cmd.exe /d /c \"{fileName}\"{extra}");
        }

        var flags = auto ? DetectExeSilentFlags(fullPath) : customArgs;
        return (fullPath, flags, $"{fileName} {flags}".Trim());
    }

    private static string DetectExeSilentFlags(string exePath)
    {
        try
        {
            // Inspect up to first 4 MB of the binary for known installer engine markers
            using var fs = File.OpenRead(exePath);
            var len = (int)Math.Min(fs.Length, 4 * 1024 * 1024);
            var buf = new byte[len];
            _ = fs.Read(buf, 0, len);
            var ascii = System.Text.Encoding.Latin1.GetString(buf);

            if (ascii.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase))
                return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
            if (ascii.Contains("Nullsoft", StringComparison.OrdinalIgnoreCase) || ascii.Contains("NSIS", StringComparison.Ordinal))
                return "/S";
            if (ascii.Contains("WixBurn", StringComparison.OrdinalIgnoreCase) || ascii.Contains("Burn v", StringComparison.OrdinalIgnoreCase))
                return "/quiet /norestart";
            if (ascii.Contains("InstallShield", StringComparison.OrdinalIgnoreCase))
                return "/s /v\"/qn /norestart\"";
        }
        catch { }

        return "/S";
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
            return Task.FromResult(new KillProcessResult
            {
                Success = true,
                Message = $"Terminated '{name}' (PID {request.Pid})."
            });
        }
        catch (Exception ex)
        {
            log.Write($"[process] '{op}' failed to terminate PID {request.Pid}: {ex.Message}");
            return Task.FromResult(new KillProcessResult
            {
                Success = false,
                Message = ex.Message
            });
        }
    }

    public override Task<ClipboardData> GetClipboard(Empty request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        var data = RunSta(() =>
        {
            var res = new ClipboardData();
            if (Clipboard.ContainsFileDropList())
            {
                long total = 0;
                foreach (string? path in Clipboard.GetFileDropList())
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    var fi = new FileInfo(path);
                    if (total + fi.Length > 25 * 1024 * 1024) break;
                    total += fi.Length;
                    res.Files.Add(new ClipboardFile
                    {
                        FileName = fi.Name,
                        Data = Google.Protobuf.ByteString.CopyFrom(File.ReadAllBytes(path))
                    });
                }
            }
            if (res.Files.Count == 0 && Clipboard.ContainsText())
                res.Text = Clipboard.GetText();
            return res;
        }) ?? new ClipboardData();

        log.Write(data.Files.Count > 0
            ? $"[clipboard] '{op}' copied {data.Files.Count} file(s) from this computer"
            : $"[clipboard] '{op}' copied clipboard text from this computer ({data.Text.Length} chars)");
        return Task.FromResult(data);
    }

    public override Task<ClipboardAck> SetClipboard(ClipboardData request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        if (request.Files.Count > 0)
        {
            var stageDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HomeRMS", "ClipboardStaging", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(stageDir);

            var paths = new System.Collections.Specialized.StringCollection();
            foreach (var f in request.Files)
            {
                var safeName = Path.GetFileName(f.FileName);
                if (string.IsNullOrWhiteSpace(safeName)) continue;
                var dest = Path.Combine(stageDir, safeName);
                File.WriteAllBytes(dest, f.Data.ToByteArray());
                paths.Add(dest);
            }

            var filesOk = paths.Count > 0 && RunSta(() =>
            {
                Clipboard.SetFileDropList(paths);
                return true;
            });
            log.Write($"[clipboard] '{op}' pasted {paths.Count} file(s) to this computer's clipboard (CF_HDROP)");
            return Task.FromResult(new ClipboardAck { Success = filesOk });
        }

        var text = request.Text ?? "";
        if (text.Length > 1024 * 1024)
            text = text[..(1024 * 1024)];

        var ok = RunSta(() =>
        {
            if (string.IsNullOrEmpty(text))
                Clipboard.Clear();
            else
                Clipboard.SetText(text);
            return true;
        });

        if (ok) Volatile.Write(ref _lastClipboardFromOperator, text);
        log.Write($"[clipboard] '{op}' pasted clipboard text to this computer ({text.Length} chars)");
        return Task.FromResult(new ClipboardAck { Success = ok });
    }

    // Text the operator last put on this PC's clipboard — not echoed back by WatchClipboard
    private static string? _lastClipboardFromOperator;

    /// <summary>
    /// Live clipboard: sends text to the operator every time something is copied on this PC
    /// (Ctrl+C, right-click → Copy, any app). Only while the operator holds an active grant;
    /// every transfer is written to the visible activity log.
    /// </summary>
    public override async Task WatchClipboard(Empty request, IServerStreamWriter<ClipboardData> responseStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        if (!consent.HasActiveGrant(op))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "No active grant. Start screen sharing first."));

        log.Write($"[clipboard] live copy/paste sync started for '{op}'");
        var lastSeq = GetClipboardSequenceNumber();
        string? lastSent = null;
        try
        {
            while (!context.CancellationToken.IsCancellationRequested && consent.HasActiveGrant(op))
            {
                await Task.Delay(250, context.CancellationToken);

                var seq = GetClipboardSequenceNumber();
                if (seq == lastSeq) continue;
                lastSeq = seq;

                var text = RunSta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
                if (string.IsNullOrEmpty(text) || text == lastSent || text == Volatile.Read(ref _lastClipboardFromOperator))
                    continue;
                if (text.Length > 1024 * 1024)
                    text = text[..(1024 * 1024)];

                lastSent = text;
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    public override Task<PairResult> PairWith2Fa(PairRequest request, ServerCallContext context)
    {
        var cert = context.GetHttpContext().Connection.ClientCertificate;
        var op = OperatorOf(context);
        var cfg = TwoFactorStore.LoadAgent();

        if (!TotpHelper.VerifyCode(cfg.TotpSecret, request.TotpCode))
        {
            log.Write($"[security] FAILED 2FA pairing attempt from '{op}'");
            return Task.FromResult(new PairResult { Success = false, Message = "Invalid 6-digit 2FA code." });
        }

        if (cert is not null)
        {
            var fp = ControllerIdentity.GetSha256Fingerprint(cert);
            TrustedControllerStore.AddPinned(fp, string.IsNullOrWhiteSpace(request.ControllerName) ? op : request.ControllerName);
            log.Write($"[security] 2FA paired & pinned Controller '{op}' ({fp[..23]}...)");
        }

        return Task.FromResult(new PairResult { Success = true, Message = "Controller paired & pinned." });
    }

    public override Task<ClipboardAck> SetPrivacyMode(PrivacyModeRequest request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        if (!consent.RequestGrant(op, request.Enabled ? "enable privacy mode (blank screen & lock input)" : "disable privacy mode"))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Privacy mode was declined."));

        if (request.Enabled)
            PrivacyCurtain.Enable(request.BannerText);
        else
            PrivacyCurtain.Disable();

        log.Write($"[privacy] '{op}' set Privacy Mode = {request.Enabled}");
        return Task.FromResult(new ClipboardAck { Success = true });
    }

    public override async Task<SystemOpResult> SystemOp(SystemOpRequest request, ServerCallContext context)
    {
        var op = OperatorOf(context);
        var detail = string.IsNullOrWhiteSpace(request.Target) ? "" : $" target='{Trunc(request.Target)}'";
        var extra = string.IsNullOrWhiteSpace(request.Extra) ? "" : $" extra='{Trunc(request.Extra)}'";
        log.Write($"[system] '{op}' requested {request.Area}/{request.Action}{detail}{extra}");

        var result = await SystemOps.RunAsync(request, context.CancellationToken);
        log.Write($"[system] {request.Area}/{request.Action} -> {(result.Success ? "OK" : "FAILED: " + Trunc(result.Message.Split('\n')[0]))}");
        return result;

        static string Trunc(string s) => s.Length <= 120 ? s : s[..120] + "…";
    }

    private static T? RunSta<T>(Func<T> func)
    {
        T? result = default;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000);
        return result;
    }
}
