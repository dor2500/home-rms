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

    // 6) silent software deployment (DeployPackage streaming upload + silent execution)
    using (var deployCall = client.DeployPackage(deadline: DateTime.UtcNow.AddSeconds(30)))
    {
        var psPayload = System.Text.Encoding.UTF8.GetBytes("Write-Output 'SILENT_DEPLOY_OK_991'");
        await deployCall.RequestStream.WriteAsync(new PackageChunk
        {
            FileName = "verify-silent-install.ps1",
            Arguments = "AUTO",
            TimeoutSeconds = 30,
            Data = Google.Protobuf.ByteString.CopyFrom(psPayload)
        });
        await deployCall.RequestStream.CompleteAsync();
        var deployRes = await deployCall.ResponseAsync;
        Console.WriteLine($"[test] DeployPackage (.ps1) OK -> success={deployRes.Success}, exit={deployRes.ExitCode}, out='{deployRes.Output.Trim()}'");
        if (!deployRes.Success || !deployRes.Output.Contains("SILENT_DEPLOY_OK_991"))
            failures.Add("DeployPackage (.ps1) failed or did not return expected silent installer output");
    }

    // 6b) silent CMD script deployment (.cmd)
    using (var cmdCall = client.DeployPackage(deadline: DateTime.UtcNow.AddSeconds(30)))
    {
        var cmdPayload = System.Text.Encoding.UTF8.GetBytes("@echo off\r\necho SILENT_CMD_OK_552\r\n");
        await cmdCall.RequestStream.WriteAsync(new PackageChunk
        {
            FileName = "verify-silent-script.cmd",
            Arguments = "AUTO",
            TimeoutSeconds = 30,
            Data = Google.Protobuf.ByteString.CopyFrom(cmdPayload)
        });
        await cmdCall.RequestStream.CompleteAsync();
        var cmdRes = await cmdCall.ResponseAsync;
        Console.WriteLine($"[test] DeployPackage (.cmd) OK -> success={cmdRes.Success}, exit={cmdRes.ExitCode}, out='{cmdRes.Output.Trim()}'");
        if (!cmdRes.Success || !cmdRes.Output.Contains("SILENT_CMD_OK_552"))
            failures.Add("DeployPackage (.cmd) failed or did not return expected silent script output");
    }

    // 7) bidirectional clipboard sync (SetClipboard + GetClipboard)
    const string clipMarker = "CLIPBOARD_SYNC_OK_4821";
    var setClip = await client.SetClipboardAsync(new ClipboardData { Text = clipMarker }, deadline: DateTime.UtcNow.AddSeconds(5));
    var getClip = await client.GetClipboardAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(5));
    Console.WriteLine($"[test] Clipboard Sync OK -> set={setClip.Success}, got='{getClip.Text}'");
    if (!setClip.Success || getClip.Text != clipMarker)
        failures.Add("Clipboard sync round-trip did not match");

    // 8) security EKU check: verify an Agent server cert CANNOT impersonate a Controller client cert
    var agentCert = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.AgentFileName, RmsPaths.CertPassword);
    var caPublic = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);
    var ekuRejected = !DevCertificates.ValidateAgainstCa(agentCert, caPublic, DevCertificates.ClientAuthOid);
    Console.WriteLine($"[test] Security EKU Gate OK -> agent cert rejected for clientAuth={ekuRejected}");
    if (!ekuRejected)
        failures.Add("Security regression: Agent server cert was accepted for clientAuth");

    // 8b) strict controller pinning: once a unique Controller fingerprint is pinned, even the shared CA cert is rejected
    var pinFile = Path.Combine(Path.GetTempPath(), $"pin-test-{Guid.NewGuid():N}.json");
    try
    {
        var uniqueCtrl = ControllerIdentity.GetOrCreateUniqueControllerCert();
        var fp = ControllerIdentity.GetSha256Fingerprint(uniqueCtrl);
        TrustedControllerStore.AddPinned(fp, "TestOwner", pinFile);
        var pinnedOk = TrustedControllerStore.ValidateAgentPeer(uniqueCtrl, caPublic, pinFile);
        var sharedCtrl = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.ControllerFileName, RmsPaths.CertPassword);
        var unpinnedRejected = !TrustedControllerStore.ValidateAgentPeer(sharedCtrl, caPublic, pinFile);
        Console.WriteLine($"[test] Strict Pinning Gate OK -> pinnedOk={pinnedOk}, unpinnedRejected={unpinnedRejected}");
        if (!pinnedOk || !unpinnedRejected)
            failures.Add("Strict controller pinning failed to enforce exclusive trust");
    }
    finally
    {
        try { if (File.Exists(pinFile)) File.Delete(pinFile); } catch { }
    }

    // 9) system control: read-only ops work, malicious / unknown input is rejected
    var sysInfo = await client.SystemOpAsync(new SystemOpRequest { Area = "system", Action = "info" }, deadline: DateTime.UtcNow.AddSeconds(60));
    var sysRows = HomeRms.Shared.SystemCatalog.ParseTable(sysInfo.JsonData).Rows.Count;
    Console.WriteLine($"[test] SystemOp system/info OK -> success={sysInfo.Success}, rows={sysRows}, elevated={sysInfo.IsElevated}");
    if (!sysInfo.Success || sysRows < 5) failures.Add("system/info did not return data");

    var svcList = await client.SystemOpAsync(new SystemOpRequest { Area = "services", Action = "list" }, deadline: DateTime.UtcNow.AddSeconds(60));
    var svcRows = HomeRms.Shared.SystemCatalog.ParseTable(svcList.JsonData).Rows.Count;
    Console.WriteLine($"[test] SystemOp services/list OK -> {svcRows} services");
    if (!svcList.Success || svcRows < 10) failures.Add("services/list did not return data");

    var upd = await client.SystemOpAsync(new SystemOpRequest { Area = "updates", Action = "status" }, deadline: DateTime.UtcNow.AddSeconds(60));
    Console.WriteLine($"[test] SystemOp updates/status -> success={upd.Success}");
    if (!upd.Success) failures.Add("updates/status failed: " + upd.Message);

    var inject = await client.SystemOpAsync(new SystemOpRequest { Area = "services", Action = "start", Target = "Spooler; calc.exe" }, deadline: DateTime.UtcNow.AddSeconds(10));
    var badReg = await client.SystemOpAsync(new SystemOpRequest { Area = "registry", Action = "get", Target = @"HKLM:\SAM\SAM" }, deadline: DateTime.UtcNow.AddSeconds(10));
    var unknown = await client.SystemOpAsync(new SystemOpRequest { Area = "evil", Action = "run" }, deadline: DateTime.UtcNow.AddSeconds(10));
    Console.WriteLine($"[test] SystemOp input validation OK -> injection blocked={!inject.Success}, SAM blocked={!badReg.Success}, unknown blocked={!unknown.Success}");
    if (inject.Success || badReg.Success || unknown.Success)
        failures.Add("SystemOp accepted invalid/malicious input");
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
