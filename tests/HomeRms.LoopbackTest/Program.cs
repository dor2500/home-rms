using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using HomeRms.Agent.Core;
using HomeRms.Agent.Server;
using HomeRms.Shared;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;

// Automated local-loopback test: start the agent's gRPC host in-process, connect
// with a mutual-TLS client, and verify:
// 1. Agent info
// 2. Software & hardware inventory
// 3. Interactive PowerShell command round-trip
// 4. Screen streaming denied when no consent grant exists
// 5. Screen streaming succeeds and delivers valid JPEG frames once consent is granted

const int port = 7444;
var failures = new List<string>();

DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword);

var log = new ActivityLog();
log.LineAdded += line => Console.WriteLine("  agent| " + line);
var consent = new ConsentCoordinator(log);

var host = AgentHost.Build(consent, log, port);
await host.StartAsync();
Console.WriteLine($"[test] host started on {port}\n");

try
{
    var client = CreateClient(port);

    // 1) agent info
    var info = await client.GetAgentInfoAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(10));
    Console.WriteLine($"[test] GetAgentInfo OK -> {info.MachineName} / {info.LoggedInUser} / {info.PrimaryScreenWidth}x{info.PrimaryScreenHeight}");
    if (string.IsNullOrWhiteSpace(info.MachineName)) failures.Add("agent info missing machine name");

    // 2) inventory
    var inv = await client.GetInventoryAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(30));
    Console.WriteLine($"[test] GetInventory OK -> {inv.Apps.Count} apps, {inv.Processes.Count} processes, CPU='{inv.Hardware.Cpu}', RAM={inv.Hardware.TotalRamBytes / (1024 * 1024 * 1024)}GB, disks={inv.Hardware.Disks.Count}");
    if (inv.Processes.Count == 0) failures.Add("inventory returned no processes");
    if (inv.Apps.Count == 0) failures.Add("inventory returned no installed apps");

    // 3) shell
    const string marker = "HELLO_FROM_LOOPBACK_7a3f";
    using var shell = client.Shell();
    var got = new TaskCompletionSource<bool>();
    var reader = Task.Run(async () =>
    {
        await foreach (var msg in shell.ResponseStream.ReadAllAsync())
        {
            Console.Write("  shell| " + msg.Data);
            if (msg.Data.Contains(marker)) got.TrySetResult(true);
        }
    });
    await shell.RequestStream.WriteAsync(new ShellInput { Data = $"Write-Output '{marker}'" });
    var hit = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    if (hit != got.Task) failures.Add("shell did not echo marker within 15s");
    else Console.WriteLine($"\n[test] Shell OK -> received marker");
    await shell.RequestStream.WriteAsync(new ShellInput { Close = true });
    try { await shell.RequestStream.CompleteAsync(); } catch { }

    // 4) screen without consent must be denied (proves the gate works)
    try
    {
        using var stream = client.StreamScreen(new ScreenRequest { Fps = 5, Quality = 50, OperatorName = "test" });
        await stream.ResponseStream.MoveNext(CancellationToken.None);
        failures.Add("screen stream was NOT denied despite no consent UI");
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
    {
        Console.WriteLine("[test] Consent gate OK -> screen denied without a grant");
    }

    // 5) screen WITH simulated consent grant streams a real JPEG frame
    consent.AttachUi(new AutoApproveConsentUi());
    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    using (var stream = client.StreamScreen(new ScreenRequest { Fps = 10, Quality = 60, OperatorName = "loopback-test" }, cancellationToken: cts.Token))
    {
        if (await stream.ResponseStream.MoveNext(cts.Token))
        {
            var frame = stream.ResponseStream.Current;
            Console.WriteLine($"[test] StreamScreen OK -> frame #{frame.Sequence} ({frame.Width}x{frame.Height}, {frame.Jpeg.Length} bytes JPEG)");
            if (frame.Jpeg.Length < 100 || frame.Width <= 0 || frame.Height <= 0)
                failures.Add("streamed frame had invalid dimensions or payload");
        }
        else
        {
            failures.Add("StreamScreen ended without delivering a frame");
        }
    }
}
catch (Exception ex)
{
    failures.Add("unexpected exception: " + ex.Message);
}
finally
{
    await host.StopAsync();
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("==== LOOPBACK TEST PASSED ====");
    return 0;
}
Console.WriteLine("==== LOOPBACK TEST FAILED ====");
foreach (var f in failures) Console.WriteLine("  - " + f);
return 1;

static RemoteManagement.RemoteManagementClient CreateClient(int port)
{
    var clientCert = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.ControllerFileName, RmsPaths.CertPassword);
    var ca = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);

    var handler = new SocketsHttpHandler
    {
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            ClientCertificates = new X509CertificateCollection { clientCert },
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                return chain.Build(cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert)));
            }
        }
    };

    var channel = GrpcChannel.ForAddress($"https://localhost:{port}",
        new GrpcChannelOptions { HttpHandler = handler, MaxReceiveMessageSize = 32 * 1024 * 1024 });
    return new RemoteManagement.RemoteManagementClient(channel);
}

sealed class AutoApproveConsentUi : IConsentUi
{
    public bool AskControlConsent(string operatorName, string action) => true;
    public void SetViewingActive(bool active, string? operatorName) { }
}
