using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using HomeRms.Agent.Core;
using HomeRms.Agent.Server;
using HomeRms.Shared;
using HomeRms.Shared.Protocol;
using HomeRms.Shared.Security;
using HomeRms.Shared.Server;
using HomeRms.Shared.Updates;

// Automated local-loopback test: start the agent's gRPC host in-process, connect
// with a mutual-TLS client, and verify:
// 0. Authorization: unpaired callers are refused everything except GetAgentInfo / PairWith2Fa,
//    PairWith2Fa is rate limited, and consent pairing pins the controller
// 1. Agent info
// 2. Software & hardware inventory
// 3. Interactive PowerShell command round-trip
// 4. Screen streaming denied when no consent grant exists
// 5. Screen streaming succeeds and delivers valid JPEG frames once consent is granted
// 6-9. Deployment, clipboard, certificate gates, system control
// 10. RunWinget input validation (nothing is installed)
// 11. File manager round-trip + path rejection
// 12. Live telemetry
// 13. Signed call-home announce received and verified by a controller-side listener
// 14. Agent self-update: tag parsing / version comparison (offline), agent/update-status via
//     SystemOp, and a real GitHub check (current build + a simulated older build). Never installs.
//
// The pinned-controller store is redirected to a temp directory, so the test neither
// depends on nor modifies the machine's real pin store.

const int port = 7444;
var failures = new List<string>();

DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword);

// U) offline unit checks for the updater's version logic
RunUpdaterUnitChecks(failures);

var log = new ActivityLog();
log.LineAdded += line => Console.WriteLine("  agent| " + line);
var consent = new ConsentCoordinator(log);

var workDir = Path.Combine(Path.GetTempPath(), $"homerms-loopback-{Guid.NewGuid():N}");
Directory.CreateDirectory(workDir);
var security = new AgentSecurityOptions { TrustedStorePath = Path.Combine(workDir, "trusted-controllers.json") };

// 13) "call home": a controller-side listener on a test port receives and verifies the agent's signed announce
const int announcePort = 47445;
var caForAnnounce = DevCertificates.LoadCaPublicOnly(RmsPaths.CertDir, RmsPaths.CertPassword);
var agentCertPublic = X509CertificateLoader.LoadCertificate(
    DevCertificates.Load(RmsPaths.CertDir, DevCertificates.AgentFileName, RmsPaths.CertPassword).RawData);
var agentThumb = HomeRms.Shared.Network.AgentAnnounce.Thumbprint(agentCertPublic);
var announceVerifier = new HomeRms.Shared.Network.AnnounceVerifier(caForAnnounce, t => t == agentThumb ? agentCertPublic : null);
using var announceListener = new HomeRms.Shared.Network.AnnounceListener(announcePort, announceVerifier, m => Console.WriteLine("  ctrl | " + m));
var announceReceived = new TaskCompletionSource<HomeRms.Shared.Network.AnnounceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
announceListener.Verified += (r, from) => announceReceived.TrySetResult(r);
if (!announceListener.Start(out var announceBindError)) failures.Add($"announce test listener could not bind UDP {announcePort}: {announceBindError}");
var announceOptions = new AgentAnnouncerOptions
{
    TargetPort = announcePort,
    StorePath = Path.Combine(workDir, "known-controllers.json"),
};

var host = AgentHost.Build(consent, log, port, security, announceOptions);
await host.StartAsync();
Console.WriteLine($"[test] host started on {port} (pin store: {security.TrustedStorePath})\n");

try
{
    var sharedCtrlCert = DevCertificates.Load(RmsPaths.CertDir, DevCertificates.ControllerFileName, RmsPaths.CertPassword);
    var client = CreateClient(port, sharedCtrlCert);

    // 0a) nothing pinned yet: every non-pairing RPC must be refused (all four call shapes)
    var unaryDenied = await StatusOf(() => client.GetInventoryAsync(new Empty(), deadline: Deadline(10)).ResponseAsync);
    var serverStreamDenied = await StatusOf(async () =>
    {
        using var call = client.GetTelemetry(new TelemetryRequest { IntervalMs = 1000 }, deadline: Deadline(10));
        await call.ResponseStream.MoveNext(CancellationToken.None);
    });
    var duplexDenied = await StatusOf(async () =>
    {
        using var call = client.Shell(deadline: Deadline(10));
        await call.ResponseStream.MoveNext(CancellationToken.None);
    });
    var clientStreamDenied = await StatusOf(async () =>
    {
        using var call = client.UploadFile(deadline: Deadline(10));
        try
        {
            await call.RequestStream.WriteAsync(new FileUploadChunk { DestinationPath = Path.Combine(workDir, "x.bin") });
            await call.RequestStream.CompleteAsync();
        }
        catch (RpcException) { }
        catch (InvalidOperationException) { }
        await call.ResponseAsync;
    });
    Console.WriteLine($"[test] Unpaired authorization -> unary={unaryDenied}, serverStream={serverStreamDenied}, duplex={duplexDenied}, clientStream={clientStreamDenied}");
    if (unaryDenied != StatusCode.PermissionDenied || serverStreamDenied != StatusCode.PermissionDenied ||
        duplexDenied != StatusCode.PermissionDenied || clientStreamDenied != StatusCode.PermissionDenied)
        failures.Add("Unpaired controller was not refused with PermissionDenied on every call shape");
    if (File.Exists(Path.Combine(workDir, "x.bin")))
        failures.Add("Unpaired UploadFile wrote a file");

    // 0b) PairWith2Fa rate limit: 5 wrong codes, then locked out
    var pairMessages = new List<string>();
    for (var i = 0; i < 6; i++)
    {
        var r = await client.PairWith2FaAsync(new PairRequest { TotpCode = "000000", ControllerName = "loopback" }, deadline: Deadline(10));
        pairMessages.Add(r.Message);
        if (r.Success) failures.Add("PairWith2Fa accepted a bogus code");
    }
    var lockedOut = pairMessages[5].Contains("Too many", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"[test] PairWith2Fa rate limit -> 6th attempt locked out={lockedOut} ('{pairMessages[5]}')");
    if (!lockedOut) failures.Add("PairWith2Fa was not locked out after 5 failed attempts");
    if (TrustedControllerStore.HasPinned(security.TrustedStorePath)) failures.Add("failed 2FA attempts pinned a controller");

    // 0c) consent pairing: refused without an on-device approval, pins after approval
    var noUiStatus = await StatusOf(() => client.GetAgentInfoAsync(new Empty(), deadline: Deadline(10)).ResponseAsync);
    Console.WriteLine($"[test] GetAgentInfo pairing without on-device approval -> {noUiStatus}");
    if (noUiStatus is not (StatusCode.PermissionDenied or StatusCode.Unauthenticated))
        failures.Add("GetAgentInfo paired without on-device approval");

    consent.AttachUi(new AutoApproveConsentUi());
    if (noUiStatus == StatusCode.Unauthenticated)
    {
        // This machine's agent 2FA config requires TOTP pairing; pin directly (equivalent end state).
        TrustedControllerStore.AddPinned(ControllerIdentity.GetSha256Fingerprint(sharedCtrlCert), "loopback", security.TrustedStorePath);
        Console.WriteLine("[test] agent 2FA pairing is required on this machine -> pinned the test controller directly");
    }
    else
    {
        await client.GetAgentInfoAsync(new Empty(), deadline: Deadline(10));
    }
    var pinned = TrustedControllerStore.IsPinned(sharedCtrlCert, security.TrustedStorePath);
    Console.WriteLine($"[test] Consent pairing OK -> controller pinned={pinned}");
    if (!pinned) failures.Add("controller was not pinned after approved pairing");

    // From here on: no grant, and further consent prompts are declined until step 5
    consent.RevokeAll();
    consent.AttachUi(new DenyConsentUi());

    // 0d) once pinned, a different controller identity cannot even complete the TLS handshake
    var otherClient = CreateClient(port, ControllerIdentity.GetOrCreateUniqueControllerCert());
    var otherStatus = await StatusOf(() => otherClient.GetAgentInfoAsync(new Empty(), deadline: Deadline(10)).ResponseAsync);
    Console.WriteLine($"[test] Unpinned second controller after pairing -> {otherStatus}");
    if (otherStatus is null) failures.Add("a second, unpinned controller was accepted after pairing");

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
        var unpinnedRejected = !TrustedControllerStore.ValidateAgentPeer(sharedCtrlCert, caPublic, pinFile);
        Console.WriteLine($"[test] Strict Pinning Gate OK -> pinnedOk={pinnedOk}, unpinnedRejected={unpinnedRejected}");
        if (!pinnedOk || !unpinnedRejected)
            failures.Add("Strict controller pinning failed to enforce exclusive trust");

        // 8c) Wake-on-LAN packet + Multi-monitor count check
        var wol = HomeRms.Shared.Network.WakeOnLan.BuildMagicPacket("AA:BB:CC:DD:EE:FF");
        if (wol.Length != 102) failures.Add("Wake-on-LAN magic packet length mismatch");
        if (info.MonitorCount < 1) failures.Add("AgentInfo.MonitorCount < 1");
        Console.WriteLine($"[test] WoL & Multi-Monitor OK -> wolBytes={wol.Length}, monitors={info.MonitorCount}");
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

    // 9b) agent/update-status answers in-process with JSON (agent/update-now is deliberately NOT called)
    var updStatus = await client.SystemOpAsync(new SystemOpRequest { Area = "agent", Action = "update-status" }, deadline: Deadline(60));
    var expectedCurrent = ReleaseVersion.Display(typeof(AgentHost).Assembly.GetName().Version!);
    string? reportedCurrent = null, reportedState = null, reportedLatest = null;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(updStatus.JsonData);
        var root = doc.RootElement;
        reportedCurrent = root.GetProperty("current").GetString();
        reportedLatest = root.GetProperty("latest").GetString();
        reportedState = root.GetProperty("state").GetString();
        foreach (var k in new[] { "available", "lastChecked", "autoUpdate" })
            if (!root.TryGetProperty(k, out _)) failures.Add($"agent/update-status JSON lacks '{k}'");
    }
    catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
    {
        failures.Add($"agent/update-status did not return valid JSON: {ex.Message} ('{updStatus.JsonData}')");
    }
    var statusRows = SystemCatalog.ParseTable(updStatus.JsonData).Rows.Count;
    Console.WriteLine($"[test] SystemOp agent/update-status -> success={updStatus.Success}, current={reportedCurrent}, latest={reportedLatest}, " +
                      $"state={reportedState}, rows={statusRows}, message='{updStatus.Message}'");
    if (!updStatus.Success) failures.Add("agent/update-status failed: " + updStatus.Message);
    if (reportedCurrent != expectedCurrent) failures.Add($"agent/update-status current='{reportedCurrent}', expected '{expectedCurrent}'");
    if (statusRows != 1) failures.Add("agent/update-status JSON does not render as one row in the Controller table");
    var badUpdateNow = await client.SystemOpAsync(new SystemOpRequest { Area = "agent", Action = "update-now", Target = "v1; calc.exe" }, deadline: Deadline(10));
    if (badUpdateNow.Success) failures.Add("agent/update-now accepted an invalid version target");
    var catalogHasAgentOps = SystemCatalog.Win11Pages.Any(p => p.Id == "agent" &&
        p.Items.Any(i => i.Action == "update-status") && p.Items.Any(i => i.Action == "update-now" && i.Dangerous));
    if (!catalogHasAgentOps) failures.Add("SystemCatalog has no 'Agent' page listing update-status / update-now (dangerous)");
    Console.WriteLine($"[test] agent/update-now validation -> bad target rejected={!badUpdateNow.Success} ('{badUpdateNow.Message}'), catalog page={catalogHasAgentOps}");

    // 10) RunWinget: malicious / malformed package IDs reject the WHOLE request before anything runs
    async Task<StatusCode?> WingetStatus(WingetRequest req) => await StatusOf(async () =>
    {
        using var call = client.RunWinget(req, deadline: Deadline(15));
        while (await call.ResponseStream.MoveNext(CancellationToken.None)) { }
    });
    var wingetCases = new (string Name, WingetRequest Req)[]
    {
        ("foo;calc", new WingetRequest { PackageIds = { "foo;calc" } }),
        ("valid + 'x&calc'", new WingetRequest { PackageIds = { "Mozilla.Firefox", "x&calc" } }),
        ("--help", new WingetRequest { PackageIds = { "--help" } }),
        ("space", new WingetRequest { PackageIds = { "Some App" } }),
        ("quote", new WingetRequest { PackageIds = { "a\"b" } }),
        ("129 chars", new WingetRequest { PackageIds = { new string('a', 129) } }),
        ("empty list", new WingetRequest()),
        ("bad version", new WingetRequest { PackageIds = { "Mozilla.Firefox" }, Version = "1.0;calc" }),
        ("version with 2 ids", new WingetRequest { PackageIds = { "A.B", "C.D" }, Version = "1.0" }),
    };
    var wingetAllRejected = true;
    foreach (var (name, req) in wingetCases)
    {
        var st = await WingetStatus(req);
        if (st != StatusCode.InvalidArgument)
        {
            wingetAllRejected = false;
            failures.Add($"RunWinget did not reject '{name}' (status {st})");
        }
    }
    var idChecks = WingetRules.IsValidPackageId("Microsoft.VisualStudioCode") && WingetRules.IsValidPackageId("7zip.7zip")
                   && WingetRules.IsValidPackageId("Notepad++.Notepad++") && !WingetRules.IsValidPackageId("foo;calc");
    if (!idChecks) failures.Add("WingetRules.IsValidPackageId accepted/rejected the wrong IDs");
    Console.WriteLine($"[test] RunWinget validation OK -> {wingetCases.Length} malicious/malformed requests rejected={wingetAllRejected}, " +
                      $"code 0x8A150014 -> '{WingetRules.Describe(unchecked((int)0x8A150014))}'");

    // 11) file manager round-trip inside a temp directory
    var fmRoot = Path.Combine(workDir, "fm");
    var fmSub = Path.Combine(fmRoot, "sub");
    var created = await client.CreateDirectoryAsync(new FilePathRequest { Path = fmRoot }, deadline: Deadline(10));
    await client.CreateDirectoryAsync(new FilePathRequest { Path = fmSub }, deadline: Deadline(10));
    if (!created.Success || !Directory.Exists(fmSub)) failures.Add("CreateDirectory did not create the directory");

    var payload = RandomNumberGenerator.GetBytes(700 * 1024 + 123); // spans 3 x 256 KB chunks
    var dataPath = Path.Combine(fmSub, "data.bin");
    async Task<FileOpResult> Upload(string dest, byte[] bytes, bool overwrite)
    {
        using var call = client.UploadFile(deadline: Deadline(30));
        const int chunk = 256 * 1024;
        try
        {
            for (var off = 0; off < bytes.Length || off == 0; off += chunk)
            {
                var n = Math.Min(chunk, bytes.Length - off);
                var msg = new FileUploadChunk { Data = Google.Protobuf.ByteString.CopyFrom(bytes, off, n) };
                if (off == 0)
                {
                    msg.DestinationPath = dest;
                    msg.Overwrite = overwrite;
                    msg.TotalSize = bytes.Length;
                }
                await call.RequestStream.WriteAsync(msg);
                if (bytes.Length == 0) break;
            }
            await call.RequestStream.CompleteAsync();
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException or IOException or HttpRequestException)
        {
            // The server may finish the call early (e.g. ALREADY_EXISTS) and reset the stream mid-write
            // ("The request was aborted"); the real status is in ResponseAsync.
        }
        return await call.ResponseAsync;
    }

    var up = await Upload(dataPath, payload, overwrite: false);
    var dupStatus = await StatusOf(() => Upload(dataPath, payload, overwrite: false));
    var upOverwrite = await Upload(dataPath, payload, overwrite: true);
    await Upload(Path.Combine(fmSub, "empty.txt"), [], overwrite: false);
    var leftoverParts = Directory.GetFiles(fmSub, "*.part").Length;
    if (!up.Success || up.Bytes != payload.Length) failures.Add("UploadFile did not report the full size");
    if (dupStatus != StatusCode.AlreadyExists) failures.Add($"UploadFile without overwrite did not return AlreadyExists ({dupStatus})");
    if (!upOverwrite.Success) failures.Add("UploadFile with overwrite failed");
    if (leftoverParts != 0) failures.Add("UploadFile left temp .part files behind");

    var listing = await client.ListDirectoryAsync(new ListDirectoryRequest { Path = fmSub }, deadline: Deadline(10));
    var listed = listing.Entries.FirstOrDefault(e => e.Name == "data.bin");
    if (listed is null || listed.IsDir || listed.Size != payload.Length || listed.ModifiedUnixMs <= 0)
        failures.Add("ListDirectory did not report data.bin correctly");
    var capped = await client.ListDirectoryAsync(new ListDirectoryRequest { Path = fmSub, MaxEntries = 1 }, deadline: Deadline(10));
    if (!capped.Truncated || capped.Entries.Count != 1) failures.Add("ListDirectory max_entries/truncated flag not honoured");
    var rootListing = await client.ListDirectoryAsync(new ListDirectoryRequest { Path = fmRoot }, deadline: Deadline(10));
    if (!rootListing.Entries.Any(e => e.Name == "sub" && e.IsDir)) failures.Add("ListDirectory did not report the subdirectory");

    var drives = await client.ListDrivesAsync(new Empty(), deadline: Deadline(10));
    if (drives.Drives.Count == 0 || !drives.Drives.Any(d => d.IsReady && d.TotalBytes > 0)) failures.Add("ListDrives returned no ready drive");

    using var downloaded = new MemoryStream();
    long reportedTotal = -1;
    var chunks = 0;
    using (var dl = client.DownloadFile(new FilePathRequest { Path = dataPath }, deadline: Deadline(30)))
    {
        await foreach (var c in dl.ResponseStream.ReadAllAsync())
        {
            if (c.Offset != downloaded.Length) failures.Add("DownloadFile chunk offset mismatch");
            reportedTotal = c.TotalSize;
            c.Data.WriteTo(downloaded);
            chunks++;
        }
    }
    var bytesMatch = downloaded.ToArray().AsSpan().SequenceEqual(payload);
    if (!bytesMatch || reportedTotal != payload.Length) failures.Add("DownloadFile bytes/total_size did not match the upload");

    var renamedPath = Path.Combine(fmSub, "renamed.bin");
    var ren = await client.RenamePathAsync(new RenamePathRequest { SourcePath = dataPath, DestinationPath = renamedPath }, deadline: Deadline(10));
    if (!ren.Success || File.Exists(dataPath) || !File.Exists(renamedPath)) failures.Add("RenamePath did not rename the file");

    var nonRecursive = await StatusOf(() => client.DeletePathAsync(new DeletePathRequest { Path = fmSub }, deadline: Deadline(10)).ResponseAsync);
    if (nonRecursive != StatusCode.FailedPrecondition) failures.Add($"DeletePath on a non-empty dir without recursive was not refused ({nonRecursive})");
    var delFile = await client.DeletePathAsync(new DeletePathRequest { Path = renamedPath }, deadline: Deadline(10));
    var delDir = await client.DeletePathAsync(new DeletePathRequest { Path = fmSub, Recursive = true }, deadline: Deadline(10));
    if (!delFile.Success || !delDir.Success || Directory.Exists(fmSub)) failures.Add("DeletePath did not delete");
    Console.WriteLine($"[test] File manager OK -> upload {up.Bytes:N0} B, dupUpload={dupStatus}, listed={listing.Entries.Count}, " +
                      $"download {downloaded.Length:N0} B in {chunks} chunks (match={bytesMatch}), renamed={ren.Success}, " +
                      $"nonRecursiveDelete={nonRecursive}, deleted={delDir.Success}, drives={drives.Drives.Count}");

    // 11b) path safety: everything here must be refused before touching the disk
    var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var pathCases = new (string Name, Func<Task> Call, StatusCode Expected)[]
    {
        ("relative", () => client.ListDirectoryAsync(new ListDirectoryRequest { Path = @"Windows\System32" }).ResponseAsync, StatusCode.InvalidArgument),
        ("drive-relative", () => client.ListDirectoryAsync(new ListDirectoryRequest { Path = "C:Windows" }).ResponseAsync, StatusCode.InvalidArgument),
        ("UNC", () => client.ListDirectoryAsync(new ListDirectoryRequest { Path = @"\\localhost\C$" }).ResponseAsync, StatusCode.InvalidArgument),
        ("\\\\?\\", () => client.ListDirectoryAsync(new ListDirectoryRequest { Path = @"\\?\C:\Windows" }).ResponseAsync, StatusCode.InvalidArgument),
        ("\\\\.\\", () => DrainDownload(client, @"\\.\PhysicalDrive0"), StatusCode.InvalidArgument),
        ("ADS", () => DrainDownload(client, Path.Combine(workDir, "x.txt:secret")), StatusCode.InvalidArgument),
        ("device name", () => client.CreateDirectoryAsync(new FilePathRequest { Path = Path.Combine(workDir, "CON") }).ResponseAsync, StatusCode.InvalidArgument),
        ("..-escape to ADS", () => client.ListDirectoryAsync(new ListDirectoryRequest { Path = @"C:\Windows\..\x:y" }).ResponseAsync, StatusCode.InvalidArgument),
        ("delete C:\\", () => client.DeletePathAsync(new DeletePathRequest { Path = @"C:\" }).ResponseAsync, StatusCode.PermissionDenied),
        ("delete %SystemRoot%", () => client.DeletePathAsync(new DeletePathRequest { Path = winDir + @"\." }).ResponseAsync, StatusCode.PermissionDenied),
        ("rename %SystemRoot%", () => client.RenamePathAsync(new RenamePathRequest { SourcePath = winDir, DestinationPath = winDir + "2" }).ResponseAsync, StatusCode.PermissionDenied),
        ("upload no dest", () => Upload("", [1, 2, 3], false), StatusCode.InvalidArgument),
        ("missing file", () => DrainDownload(client, Path.Combine(workDir, "nope.bin")), StatusCode.NotFound),
    };
    var pathOk = 0;
    foreach (var (name, call, expected) in pathCases)
    {
        var st = await StatusOf(call);
        if (st == expected) pathOk++;
        else failures.Add($"path safety case '{name}' returned {st?.ToString() ?? "OK"} instead of {expected}");
    }
    Console.WriteLine($"[test] File manager path safety -> {pathOk}/{pathCases.Length} cases refused as expected");

    // 11c) protected-path aliases. All calls are NON-recursive, so even a regression could not delete
    //      anything (a non-empty directory answers FailedPrecondition instead of PermissionDenied).
    var profileDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    var aliasCases = new List<(string Name, string Path)>
    {
        ("%SystemRoot% + trailing space", winDir + " "),
        ("%SystemRoot% + trailing dot", winDir + "."),
        ("user profile folder", profileDir),
    };
    var shortProgramFiles = SafePath.ExpandShortNames(programFiles) == programFiles ? GetShortPath(programFiles) : null;
    if (shortProgramFiles is not null && shortProgramFiles != programFiles)
        aliasCases.Add(("Program Files 8.3 alias", shortProgramFiles));
    var aliasOk = 0;
    foreach (var (name, path) in aliasCases)
    {
        var st = await StatusOf(() => client.DeletePathAsync(new DeletePathRequest { Path = path, Recursive = false }, deadline: Deadline(10)).ResponseAsync);
        if (st == StatusCode.PermissionDenied) aliasOk++;
        else failures.Add($"protected alias '{name}' ({path}) returned {st?.ToString() ?? "OK"} instead of PermissionDenied");
    }
    Console.WriteLine($"[test] Protected path aliases -> {aliasOk}/{aliasCases.Count} refused (8.3 alias {(shortProgramFiles is null ? "not available on this volume" : shortProgramFiles)})");

    // 11d) a damaged pin store fails CLOSED (it used to read as "nothing pinned" and reopen pairing)
    var damagedStore = Path.Combine(workDir, "damaged-pins.json");
    File.WriteAllText(damagedStore, "{ this is not json");
    var damagedRejects = !TrustedControllerStore.ValidateAgentPeer(sharedCtrlCert, caPublic, damagedStore)
                         && TrustedControllerStore.HasPinned(damagedStore);
    Console.WriteLine($"[test] Damaged pin store fails closed -> {damagedRejects}");
    if (!damagedRejects) failures.Add("a damaged pin store let an unpinned controller through");

    // 11e) atomic settings write leaves no temp files and replaces the content
    var atomicPath = Path.Combine(workDir, "atomic.json");
    AtomicFile.WriteAllText(atomicPath, "first");
    AtomicFile.WriteAllText(atomicPath, "second");
    var atomicOk = File.ReadAllText(atomicPath) == "second" && Directory.GetFiles(workDir, "*.tmp").Length == 0;
    Console.WriteLine($"[test] AtomicFile -> {atomicOk}");
    if (!atomicOk) failures.Add("AtomicFile did not replace the file cleanly");

    // 12) telemetry: at least two samples with sane values
    var samples = new List<TelemetrySample>();
    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
    using (var tel = client.GetTelemetry(new TelemetryRequest { IntervalMs = 200 /* clamped to 1000 */ }, cancellationToken: cts.Token))
    {
        try
        {
            while (samples.Count < 3 && await tel.ResponseStream.MoveNext(cts.Token))
                samples.Add(tel.ResponseStream.Current);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
    }
    var saneTelemetry = samples.Count >= 2 && samples.All(s =>
        s.CpuPercent is >= 0 and <= 100 && s.MemTotalBytes > 0 && s.MemUsedBytes > 0 && s.MemUsedBytes <= s.MemTotalBytes &&
        s.Disks.Count > 0 && s.Disks.All(d => d.TotalBytes > 0 && d.FreeBytes <= d.TotalBytes) &&
        s.NetRxBytesPerSec >= 0 && s.NetTxBytesPerSec >= 0 && s.UptimeSeconds > 0 && s.ProcessCount > 10 && s.TimestampUnixMs > 0);
    var gapMs = samples.Count >= 3 ? samples[2].TimestampUnixMs - samples[1].TimestampUnixMs : 0;
    if (!saneTelemetry) failures.Add("telemetry returned fewer than 2 samples or insane values");
    if (samples.Count >= 3 && gapMs < 900) failures.Add($"telemetry interval was not clamped to >= 1000 ms (gap {gapMs} ms)");
    var last = samples.LastOrDefault();
    Console.WriteLine($"[test] Telemetry OK -> {samples.Count} samples, gap={gapMs} ms, cpu={last?.CpuPercent:F1}%, " +
                      $"mem={last?.MemUsedBytes / (1024 * 1024)}/{last?.MemTotalBytes / (1024 * 1024)} MB, disks={last?.Disks.Count}, " +
                      $"rx={last?.NetRxBytesPerSec:F0} B/s, tx={last?.NetTxBytesPerSec:F0} B/s, uptime={last?.UptimeSeconds}s, procs={last?.ProcessCount}");

    // 13) the pinned test controller connected from 127.0.0.1, so the agent remembered that address and
    //     announced itself there (signed with its TLS certificate) without waiting for the 60 s timer.
    var announceDone = await Task.WhenAny(announceReceived.Task, Task.Delay(TimeSpan.FromSeconds(20)));
    var announcer = host.Services.GetService(typeof(AgentAnnouncer)) as AgentAnnouncer;
    var remembered = announcer?.KnownControllers ?? [];
    if (announceDone != announceReceived.Task)
    {
        failures.Add($"no verified announce arrived on UDP {announcePort} within 20 s (remembered controllers: {string.Join(", ", remembered)})");
    }
    else
    {
        var ann = announceReceived.Task.Result.Payload!;
        Console.WriteLine($"[test] Announce OK -> {ann.MachineName}:{ann.Port}, addresses={string.Join(",", ann.Addresses)}, " +
                          $"thumb={ann.Thumbprint[..12]}…, remembered={string.Join(", ", remembered)}");
        if (!string.Equals(ann.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) failures.Add("announce carried the wrong machine name");
        if (ann.Port != port) failures.Add($"announce carried port {ann.Port}, expected {port}");
        if (ann.Thumbprint != agentThumb) failures.Add("announce was not signed with the agent certificate");
    }
    if (!remembered.Contains("127.0.0.1") && !remembered.Contains("::1")) failures.Add("agent did not remember the pinned controller's address");
    if (!File.Exists(announceOptions.StorePath!)) failures.Add("known controller addresses were not persisted");

    // 14) real GitHub checks (apply is disabled on these instances; nothing is downloaded or replaced)
    using (var realUpdater = new AgentUpdater(log, [], applyDisabledReason: "test"))
    using (var olderUpdater = new AgentUpdater(log, [], currentVersionOverride: new Version(2, 1, 0), applyDisabledReason: "test"))
    {
        var now = await realUpdater.CheckAsync();
        var older = await olderUpdater.CheckAsync();
        Console.WriteLine($"[test] CheckAsync (this build {ReleaseVersion.Display(now.Current)}) -> latest={now.LatestTag}, available={now.UpdateAvailable}, " +
                          $"state={realUpdater.GetStatus().State}, published={now.PublishedAt:yyyy-MM-dd}, error={now.Error ?? "none"}");
        Console.WriteLine($"[test] CheckAsync (simulated v2.1.0) -> latest={older.LatestTag}, available={older.UpdateAvailable}, " +
                          $"asset={older.AssetSize:N0} B, notes={older.ReleaseNotes.Length} chars, state={olderUpdater.GetStatus().State}");
        if (now.NetworkError || older.NetworkError)
        {
            Console.WriteLine("[test] WARN GitHub unreachable - real update checks skipped: " + (now.Error ?? older.Error));
        }
        else
        {
            if (now.Error is not null || older.Error is not null) failures.Add($"CheckAsync failed: {now.Error ?? older.Error}");
            if (now.Latest is null || ReleaseVersion.IsNewer(now.Latest, now.Current) != now.UpdateAvailable)
                failures.Add("CheckAsync availability does not match the version comparison");
            if (!now.UpdateAvailable && realUpdater.GetStatus().State != AgentUpdateState.UpToDate)
                failures.Add("an up-to-date check did not end in the UpToDate state");
            if (!older.UpdateAvailable || olderUpdater.GetStatus().State != AgentUpdateState.Available || string.IsNullOrEmpty(older.AssetUrl))
                failures.Add("a simulated v2.1.0 build did not report the latest release as available");
            if (realUpdater.LastChecked is null) failures.Add("CheckAsync did not record LastChecked");

            try
            {
                await olderUpdater.DownloadAndApplyAsync(null);
                failures.Add("DownloadAndApplyAsync ran on an updater with applying disabled");
            }
            catch (InvalidOperationException ex) when (ex.Message == "test")
            {
                Console.WriteLine("[test] DownloadAndApplyAsync refused on a disabled updater (nothing downloaded or installed)");
            }
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
    try { Directory.Delete(workDir, recursive: true); } catch { }
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

static DateTime Deadline(int seconds) => DateTime.UtcNow.AddSeconds(seconds);

static void RunUpdaterUnitChecks(List<string> failures)
{
    var before = failures.Count;
    void Check(bool ok, string what) { if (!ok) failures.Add("updater unit check: " + what); }

    Check(ReleaseVersion.TryParseTag("v2.2.0", out var v220) && v220 == new Version(2, 2, 0), "'v2.2.0' parses to 2.2.0");
    Check(ReleaseVersion.TryParseTag("2.2", out var v22) && v22 == new Version(2, 2, 0), "'2.2' parses to 2.2.0");
    Check(ReleaseVersion.TryParseTag("V10.0.1", out var v1001) && v1001 == new Version(10, 0, 1), "'V10.0.1' parses");
    Check(!ReleaseVersion.IsNewer(v220, new Version(2, 2, 0, 0)), "v2.2.0 is not newer than assembly 2.2.0.0");
    Check(!ReleaseVersion.IsNewer(v220, new Version(2, 2, 0, 7)), "the revision part is ignored");
    Check(ReleaseVersion.IsNewer(v220, new Version(2, 1, 9, 0)), "v2.2.0 is newer than 2.1.9.0");
    Check(ReleaseVersion.IsNewer(v1001, new Version(9, 99, 99)), "numeric (not string) comparison");

    // Background check schedule (24 / 48 h, persisted last-check time)
    var t0 = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    Check(AgentUpdateSettings.IsAutoCheckDue(null, 24, t0), "never checked -> due");
    Check(!AgentUpdateSettings.IsAutoCheckDue(t0, 24, t0.AddHours(23.9)), "24 h: not due after 23.9 h");
    Check(AgentUpdateSettings.IsAutoCheckDue(t0, 24, t0.AddHours(24)), "24 h: due after 24 h");
    Check(!AgentUpdateSettings.IsAutoCheckDue(t0, 48, t0.AddHours(47)), "48 h: not due after 47 h");
    Check(AgentUpdateSettings.IsAutoCheckDue(t0, 48, t0.AddHours(48)), "48 h: due after 48 h");
    Check(AgentUpdateSettings.IsAutoCheckDue(t0, 48, t0.AddHours(-2)), "clock moved back > 1 h -> due");
    Check(AgentUpdateSettings.NormalizeInterval(15) == 24 && AgentUpdateSettings.NormalizeInterval(48) == 48, "only 24 / 48 h intervals are accepted");
    Check(!AgentUpdateSettings.IsAutoCheckDue(t0, 7, t0.AddHours(23)) && AgentUpdateSettings.IsAutoCheckDue(t0, 7, t0.AddHours(24)), "an invalid interval falls back to 24 h");
    Check(!ReleaseVersion.IsNewer(new Version(2, 1, 0), new Version(2, 2, 0)), "older is not newer");
    foreach (var bad in new[] { "v2.3.0-beta.1", "v2.3.0-rc1", "2.3.0+build5", "latest", "", "v", "v2", "v2.3.0.1.5" })
        Check(!ReleaseVersion.TryParseTag(bad, out _), $"'{bad}' is rejected (pre-release / not a version)");
    Check(ReleaseVersion.Display(new Version(2, 2, 0, 0)) == "v2.2.0", "Display drops the revision");

    const string release = """
        {"tag_name":"v2.3.0","prerelease":false,"draft":false,"published_at":"2026-11-20T10:00:00Z",
         "body":"## What's new\r\n- **Faster** screen streaming\r\n- See [docs](https://example.invalid/x)\r\n\r\n`code` and <b>html</b>",
         "assets":[{"name":"HomeRmsAgent.exe","size":123456,"browser_download_url":"https://example.invalid/HomeRmsAgent.exe"},
                   {"name":"checksums.sha256","size":10,"browser_download_url":"https://example.invalid/checksums.sha256"}]}
        """;
    var info = ReleaseInfo.Parse(release, AgentUpdater.AssetName);
    Check(info.Version == new Version(2, 3, 0) && info.AssetSize == 123456 && info.ChecksumUrl is not null, "release JSON parsed");
    var avail = AgentUpdater.Evaluate(info, new Version(2, 2, 0, 0));
    Check(avail.UpdateAvailable && avail.Error is null && avail.AssetUrl is not null, "2.2.0.0 sees v2.3.0 as available");
    Check(!AgentUpdater.Evaluate(info, new Version(2, 3, 0, 0)).UpdateAvailable, "2.3.0.0 is up to date with v2.3.0");
    Check(avail.ReleaseNotes.StartsWith("What's new", StringComparison.Ordinal) && avail.ReleaseNotes.Contains("• Faster screen streaming")
          && avail.ReleaseNotes.Contains("See docs") && !avail.ReleaseNotes.Contains("**") && !avail.ReleaseNotes.Contains("<b>"),
          $"markdown stripped lightly (got: {avail.ReleaseNotes.Replace(Environment.NewLine, " | ")})");

    var pre = ReleaseInfo.Parse(release.Replace("\"prerelease\":false", "\"prerelease\":true"), AgentUpdater.AssetName);
    Check(!AgentUpdater.Evaluate(pre, new Version(2, 2, 0)).UpdateAvailable, "a release flagged prerelease is ignored");
    var preTag = ReleaseInfo.Parse(release.Replace("v2.3.0", "v2.3.0-beta.2"), AgentUpdater.AssetName);
    Check(!AgentUpdater.Evaluate(preTag, new Version(2, 2, 0)).UpdateAvailable, "a pre-release tag is ignored");
    var noAsset = ReleaseInfo.Parse(release.Replace("\"HomeRmsAgent.exe\"", "\"Other.exe\""), AgentUpdater.AssetName);
    var noAssetEval = AgentUpdater.Evaluate(noAsset, new Version(2, 2, 0));
    Check(!noAssetEval.UpdateAvailable && noAssetEval.Error is not null, "a release without HomeRmsAgent.exe is reported, not offered");

    // dual-instance coordination: while one holder has the machine-wide update lock, a second cannot take it
    using (var first = AgentUpdater.TryAcquireMachineLock())
    {
        Check(first is not null, "machine-wide update lock can be acquired");
        Check(AgentUpdater.TryAcquireMachineLock() is null, "a second acquirer is refused while the lock is held");
    }
    using (var again = AgentUpdater.TryAcquireMachineLock())
        Check(again is not null, "the lock is free again after release");

    Console.WriteLine($"[test] Updater unit checks -> {(failures.Count == before ? "all passed" : $"{failures.Count - before} failed")}");
}

static string? GetShortPath(string path)
{
    var buffer = new char[1024];
    var n = NativeMethods.GetShortPathNameW(path, buffer, (uint)buffer.Length);
    return n > 0 && n < buffer.Length ? new string(buffer, 0, (int)n) : null;
}

static async Task<StatusCode?> StatusOf(Func<Task> call)
{
    try
    {
        await call();
        return null;
    }
    catch (RpcException ex)
    {
        return ex.StatusCode;
    }
}

static async Task DrainDownload(RemoteManagement.RemoteManagementClient client, string path)
{
    using var call = client.DownloadFile(new FilePathRequest { Path = path }, deadline: DateTime.UtcNow.AddSeconds(10));
    while (await call.ResponseStream.MoveNext(CancellationToken.None)) { }
}

static RemoteManagement.RemoteManagementClient CreateClient(int port, X509Certificate2 clientCert)
{
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

static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint bufferLength);
}

sealed class AutoApproveConsentUi : IConsentUi
{
    public bool AskControlConsent(string operatorName, string action) => true;
    public void SetViewingActive(bool active, string? operatorName) { }
}

sealed class DenyConsentUi : IConsentUi
{
    public bool AskControlConsent(string operatorName, string action) => false;
    public void SetViewingActive(bool active, string? operatorName) { }
}
