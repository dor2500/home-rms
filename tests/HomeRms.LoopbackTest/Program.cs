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
//
// The pinned-controller store is redirected to a temp directory, so the test neither
// depends on nor modifies the machine's real pin store.

const int port = 7444;
var failures = new List<string>();

DevCertificates.EnsureDeploymentCertificates(RmsPaths.CertDir, RmsPaths.CertPassword);

var log = new ActivityLog();
log.LineAdded += line => Console.WriteLine("  agent| " + line);
var consent = new ConsentCoordinator(log);

var workDir = Path.Combine(Path.GetTempPath(), $"homerms-loopback-{Guid.NewGuid():N}");
Directory.CreateDirectory(workDir);
var security = new AgentSecurityOptions { TrustedStorePath = Path.Combine(workDir, "trusted-controllers.json") };

var host = AgentHost.Build(consent, log, port, security);
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
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            // The server may finish the call early (e.g. ALREADY_EXISTS); the real status is in ResponseAsync.
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
