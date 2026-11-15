using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Grpc.Core;
using HomeRms.Agent.Core;
using HomeRms.Agent.Features;
using HomeRms.Shared.Protocol;

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
        var op = OperatorOf(context);
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
            PrimaryScreenHeight = h
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

        var fps = Math.Clamp(request.Fps <= 0 ? 10 : request.Fps, 1, 30);
        var delay = TimeSpan.FromMilliseconds(1000.0 / fps);
        var quality = request.Quality <= 0 ? 60 : request.Quality;

        using var _ = consent.BeginViewing(op);
        using var cap = new ScreenCapturer();
        long seq = 0;

        while (!context.CancellationToken.IsCancellationRequested)
        {
            if (!consent.HasActiveGrant(op))
            {
                log.Write($"[screen] grant for '{op}' expired/revoked — stopping stream");
                break;
            }

            byte[] jpeg;
            try { jpeg = cap.CaptureJpeg(quality); }
            catch (Exception ex) { log.Write($"[screen] capture error: {ex.Message}"); break; }

            var (w, h) = cap.PrimaryBounds;
            await responseStream.WriteAsync(new ScreenFrame
            {
                Jpeg = Google.Protobuf.ByteString.CopyFrom(jpeg),
                Width = w,
                Height = h,
                Sequence = ++seq
            });

            try { await Task.Delay(delay, context.CancellationToken); }
            catch (OperationCanceledException) { break; }
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

    public override async Task<DeployResult> DeployPackage(
        IAsyncStreamReader<PackageChunk> requestStream, ServerCallContext context)
    {
        var op = OperatorOf(context);
        var deployDir = Path.Combine(Path.GetTempPath(), "HomeRMS-Deploy");
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
                        fileName = Path.GetFileName(chunk.FileName);
                    if (!string.IsNullOrWhiteSpace(chunk.Arguments))
                        customArgs = chunk.Arguments.Trim();
                    if (chunk.TimeoutSeconds > 0)
                        timeoutSec = Math.Clamp(chunk.TimeoutSeconds, 15, 1800);

                    if (chunk.Data.Length > 0)
                    {
                        chunk.Data.WriteTo(fs);
                        totalBytes += chunk.Data.Length;
                    }
                }
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
            var psArgs = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{fullPath}\" {(auto ? "" : customArgs)}".Trim();
            return ("powershell.exe", psArgs, $"powershell.exe -ExecutionPolicy Bypass -File \"{fileName}\"");
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
}
