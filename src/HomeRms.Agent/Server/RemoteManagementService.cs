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
}
