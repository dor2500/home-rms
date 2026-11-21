using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;
using HomeRms.Shared.Server;

// Offline checks of controller-side logic that the agent loopback test cannot reach:
// 1. Every PowerShell script the controller generates from data (winget over WinRM, the WinRM
//    wrapper itself, Script Library wrappers) parses without errors with PowerShell's own parser,
//    including hostile inputs (quotes, here-string terminators, braces, '$', backticks).
// 2. WinRM credentials survive the round trip unchanged (the old here-string doubled quotes).
// 3. Script Library wrappers really run and report the completion marker.
// 4. winstall / winget import parsing and package-ID validation.
// 5. Central Hub relay access control (sign-in, CSRF header, no open relay / proxy).
// 6-8. Endpoint addressing, signed announces, Update All output parsing (winget + Linux managers).
//
// Nothing here talks to a remote machine. Run: dotnet run --project tests\HomeRms.ControllerChecks

var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
    if (!ok) failures.Add(what);
}

var work = Path.Combine(Path.GetTempPath(), $"homerms-ctrlchecks-{Guid.NewGuid():N}");
Directory.CreateDirectory(work);

try
{
    // ---------------- 1. PowerShell syntax ----------------
    Console.WriteLine("[checks] PowerShell parser");
    var scripts = new Dictionary<string, string>();
    foreach (var upgrade in new[] { false, true })
    foreach (var silent in new[] { false, true })
        scripts[$"winget-install upgrade={upgrade} silent={silent}"] =
            WingetCatalogService.BuildWingetInstallScript("Notepad++.Notepad++", silent, machineScope: true, upgradeIfInstalled: upgrade);

    const string hostile = "p'a\"s$s`w}o{rd'@\n'@ x\r\n@'";
    scripts["winrm wrapper (hostile credentials)"] = AgentlessService.BuildWinRmWrapper("DOMAIN\\o'brien", hostile, "pc-01'; calc", "Get-Date");
    scripts["winrm wrapper (unbalanced braces in body)"] = AgentlessService.BuildWinRmWrapper("u", "p", "pc", "} ; Start-Process calc ; {");
    scripts["winrm wrapper (winget body)"] = AgentlessService.BuildWinRmWrapper("u", "p", "pc", scripts["winget-install upgrade=True silent=True"]);

    foreach (var s in ScriptLibraryStore.BuiltIns())
        scripts[$"library builtin '{s.Name}'"] = ScriptExecutor.BuildPowerShell(s.Shell, s.Body, s.Name);
    scripts["library: body with '@ line"] = ScriptExecutor.BuildPowerShell(ScriptShell.PowerShell, "Write-Output 'a'\n'@\nWrite-Output 'b'", "x");
    scripts["library: body with indented '@ line"] = ScriptExecutor.BuildPowerShell(ScriptShell.PowerShell, "Write-Output 'a'\n   '@\n", "x");
    scripts["library: cmd body with quotes"] = ScriptExecutor.BuildPowerShell(ScriptShell.Cmd, "echo \"a'b\" & echo '@", "t\nitle");

    foreach (var unknown in new[] { false, true })
    foreach (var silent in new[] { false, true })
        scripts[$"update-all winget upgrade --all unknown={unknown} silent={silent}"] = UpgradeAllScripts.BuildWingetUpgradeAllScript(unknown, silent);
    scripts["winrm wrapper (update-all body)"] = AgentlessService.BuildWinRmWrapper("u", "p", "pc", UpgradeAllScripts.BuildWingetUpgradeAllScript(true, true));

    scripts["winget-install pinned version (WinRM)"] =
        WingetCatalogService.BuildWingetInstallScript("Google.Chrome", silent: true, machineScope: false, upgradeIfInstalled: true, version: "158.0.8127.37");

    // Install view "PowerShell" tab: defaults, every option, hostile advanced values ($, backticks, quotes, braces).
    WingetScriptApp[] genApps =
    [
        new("Google.Chrome"),
        new("Notepad++.Notepad++", Version: "8.7.1"),
        new("7zip.7zip", Scope: InstallScope.User, Interactive: true, Force: true,
            Override: "/S /D=C:\\Program Files\\7 $(calc) `whoami` 'q' \"dq\" {x}", Log: "C:\\Logs\\it's.log", Location: "D:\\Apps\\7zip"),
    ];
    foreach (var upgrade in new[] { false, true })
    foreach (var scope in Enum.GetValues<InstallScope>())
        scripts[$"generator PowerShell scope={scope} upgrade={upgrade}"] =
            WingetScriptGenerator.PowerShell(genApps, new WingetScriptOptions(scope, Interactive: false, Force: upgrade, Silent: !upgrade, UpgradeIfInstalled: upgrade));

    var parseErrors = ParsePowerShell(work, scripts);
    foreach (var name in scripts.Keys)
        Check(!parseErrors.TryGetValue(name, out var err), $"parses: {name}{(parseErrors.TryGetValue(name, out var e2) ? " -> " + e2 : "")}");

    // ---------------- 2. Credential round trip ----------------
    Console.WriteLine("[checks] WinRM credential encoding");
    var prelude = AgentlessService.BuildCredentialPrelude("DOMAIN\\o'brien", hostile) +
                  "\n[Console]::Out.Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($cred.UserName + '|' + $cred.GetNetworkCredential().Password)))";
    var (_, credOut) = RunPowerShell(work, prelude);
    var decoded = TryDecode(credOut.Trim());
    Check(decoded == "DOMAIN\\o'brien|" + hostile, "user name and password with quotes / '@ / $ / backticks / newlines round-trip unchanged"
        + (decoded == "DOMAIN\\o'brien|" + hostile ? "" : $" -> got: {credOut.Trim()}"));

    // ---------------- 3. Script Library wrappers execute ----------------
    Console.WriteLine("[checks] Script Library wrappers");
    // The body holds its own here-string with an indented '@ line, which forces the Base64 fallback.
    var (_, psOut) = RunPowerShell(work, ScriptExecutor.BuildPowerShell(ScriptShell.PowerShell,
        "$t = @'\n  '@ indented, still inside\n'@\nWrite-Output ('LIB_OK_' + $t.Length)", "t"));
    Check(psOut.Contains("LIB_OK_") && psOut.Contains(ScriptExecutor.DoneMarker + "0|"),
        $"PowerShell snippet (with an indented '@ line) runs and prints the completion marker{(psOut.Contains("LIB_OK_") ? "" : " -> " + psOut.Trim())}");
    var (_, cmdOut) = RunPowerShell(work, ScriptExecutor.BuildPowerShell(ScriptShell.Cmd, "echo LIB_OK_2", "t"));
    Check(cmdOut.Contains("LIB_OK_2") && cmdOut.Contains(ScriptExecutor.DoneMarker + "0|0"), "cmd snippet runs and reports success");

    // ---------------- 4. Import parsing ----------------
    Console.WriteLine("[checks] winstall / winget import parsing");
    var script = WinstallImporter.ParseText("winget install --id=Git.Git -e && winget install --id \"Mozilla.Firefox\" -e ; winget install 7zip.7zip --silent\nwinget install --id=foo;calc");
    // ';' ends a command, so "--id=foo;calc" is the ID "foo" plus a separate "calc" command (ignored).
    Check(script.Ids.SequenceEqual(["Git.Git", "Mozilla.Firefox", "7zip.7zip", "foo"]), $"winget script -> {string.Join(", ", script.Ids)}");
    var export = WinstallImporter.ParseText("{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"Microsoft.PowerToys\"},{\"PackageIdentifier\":\"bad id\"}]}]}");
    Check(export.Ids.SequenceEqual(["Microsoft.PowerToys"]) && export.Rejected.Count == 1, "winget export JSON keeps valid IDs and rejects invalid ones");
    var pack = WinstallImporter.ParsePackJson("{\"name\":\"Pack\",\"apps\":[{\"_id\":\"VideoLAN.VLC\",\"name\":\"VLC\"},\"Notepad++.Notepad++\"]}");
    Check(pack.PackName == "Pack" && pack.Ids.Count == 2 && pack.Info["VideoLAN.VLC"].Name == "VLC", "winstall pack JSON");
    var list = WinstallImporter.ParseText("# comment\nGit.Git, 7zip.7zip\nhttps://winstall.app/apps/Microsoft.VisualStudioCode\n$(calc)");
    Check(list.Ids.Count == 3 && list.Rejected.Count == 1, $"ID list with app URL and junk -> ids={list.Ids.Count}, rejected={list.Rejected.Count}");
    Check(WinstallImporter.TryGetPackId("https://winstall.app/packs/abc_DEF-12", out var packId) && packId == "abc_DEF-12", "pack URL detection");
    Check(WingetRules.IsValidPackageId("Notepad++.Notepad++") && !WingetRules.IsValidPackageId("a'b") && !WingetRules.IsValidPackageId("--id"),
        "package ID validation");

    // ---------------- 4b. Install view script generators ----------------
    Console.WriteLine("[checks] Batch / PowerShell / winget import generators");
    var psGen = WingetScriptGenerator.PowerShell(genApps, new WingetScriptOptions());
    Check(psGen.Contains("winget install --id=Notepad++.Notepad++ -e -v 8.7.1 --silent") && psGen.Contains("--scope user --interactive --force")
          && !psGen.Contains("--interactive --silent"),
        "PowerShell: pinned version, per-app scope / interactive / force, interactive suppresses --silent");
    Check(psGen.Contains("--log 'C:\\Logs\\it''s.log'"), "PowerShell: single quotes are doubled inside single-quoted values");

    // The hostile override is evaluated by PowerShell as one literal argument (no $(), backtick or quote breakout).
    var echoScript = WingetScriptGenerator.PowerShell([genApps[2]], new WingetScriptOptions())
        .Replace("winget install", "& { $args | ForEach-Object { 'ARG:' + $_ } }");
    var (_, echoOut) = RunPowerShell(work, echoScript);
    Check(echoOut.Contains("ARG:/S /D=C:\\Program Files\\7 $(calc) `whoami` 'q' \"dq\" {x}"),
        $"PowerShell: --override value reaches winget unchanged{(echoOut.Contains("ARG:/S") ? "" : " -> " + echoOut.Trim())}");

    var batApps = new[] { new WingetScriptApp("Google.Chrome"), new WingetScriptApp("VideoLAN.VLC", "3.0.21", InstallScope.Machine, Override: "/S /L=100%") };
    var bat = WingetScriptGenerator.Batch(batApps, new WingetScriptOptions(UpgradeIfInstalled: true));
    Check(bat.StartsWith("@echo off\r\n") && bat.Contains("winget upgrade --id=Google.Chrome -e --silent")
          && bat.Contains($"if %ERRORLEVEL% EQU {WingetScriptGenerator.NoPackageFoundExit} winget install --id=VideoLAN.VLC -e -v 3.0.21 --scope machine")
          && bat.Contains("--override \"/S /L=100%%\""),
        "Batch: upgrade-then-install, pinned version, machine scope, % doubled inside quotes");
    var batRejected = false;
    try { WingetScriptGenerator.Batch([new WingetScriptApp("Google.Chrome", Override: "a\" & calc & \"")], new WingetScriptOptions()); }
    catch (ArgumentException) { batRejected = true; }
    Check(batRejected, "Batch: a double quote in --override is rejected (cannot be quoted safely)");
    var badIdRejected = false;
    try { WingetScriptGenerator.PowerShell([new WingetScriptApp("bad id; calc")], new WingetScriptOptions()); }
    catch (ArgumentException) { badIdRejected = true; }
    Check(badIdRejected, "generators reject invalid package IDs");
    var badVersionRejected = false;
    try { WingetScriptGenerator.Batch([new WingetScriptApp("Google.Chrome", "1.0 & calc")], new WingetScriptOptions()); }
    catch (ArgumentException) { badVersionRejected = true; }
    Check(badVersionRejected, "generators reject invalid versions");

    var importJson = WingetScriptGenerator.WingetImportJson(genApps, new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc));
    using (var doc = System.Text.Json.JsonDocument.Parse(importJson))
    {
        var pkgs = doc.RootElement.GetProperty("Sources")[0].GetProperty("Packages");
        var ids = pkgs.EnumerateArray().Select(e => e.GetProperty("PackageIdentifier").GetString()).ToList();
        var pinnedVer = pkgs[1].GetProperty("Version").GetString();
        Check(ids.SequenceEqual(genApps.Select(a => a.Id)) && pinnedVer == "8.7.1" && !pkgs[0].TryGetProperty("Version", out _)
              && doc.RootElement.GetProperty("$schema").GetString()!.Contains("winget-packages"),
            "winget import JSON: schema, ids in order, Version only when pinned");
    }
    var roundTrip = WinstallImporter.ParseText(importJson);
    Check(roundTrip.Format == "winget export JSON" && roundTrip.Ids.SequenceEqual(genApps.Select(a => a.Id)) && roundTrip.Info["Notepad++.Notepad++"].Version == "8.7.1",
        "winget import JSON round-trips through the pack importer (ids + pinned version)");
    var batRoundTrip = WinstallImporter.ParseText(bat);
    Check(batRoundTrip.Ids.SequenceEqual(["Google.Chrome", "VideoLAN.VLC"]), $"generated batch re-imports to the same IDs -> {string.Join(", ", batRoundTrip.Ids)}");
    Check(WingetScriptGenerator.SingleCommand("Google.Chrome", null, false) == "winget install -e --id Google.Chrome"
          && WingetScriptGenerator.SingleCommand("Google.Chrome", "157.0", true) == "winget upgrade -e --id Google.Chrome -v 157.0",
        "app page command matches winstall's format");

    // api.winstall.app response parsing (shapes observed on the live API)
    var (wApps, wTotal) = WinstallApiClient.ParseAppList("{\"total\":16455,\"offset\":0,\"limit\":2,\"data\":[{\"_id\":\"Google.Chrome\",\"name\":\"Google Chrome\",\"publisher\":\"Google LLC\",\"desc\":\"Fast\\nbrowser\",\"latestVersion\":\"158.0\",\"categories\":[\"browser\"]},{\"_id\":\"bad id\"}]}");
    Check(wTotal == 16455 && wApps.Count == 1 && wApps[0].Description == "Fast browser", "winstall app list: total, invalid ids dropped, control characters cleaned");
    var wDetail = WinstallApiClient.ParseAppDetails("{\"_id\":\"Google.Chrome\",\"name\":\"Google Chrome\",\"latestVersion\":\"158.0\",\"homepage\":\"javascript:alert(1)\",\"licenseUrl\":\"https://x.example/l\",\"updatedAt\":\"2026-11-19T04:15:03.938Z\",\"versions\":[{\"version\":\"158.0\"},{\"version\":\"157.0\"},{\"version\":\"1 & calc\"}]}");
    Check(wDetail.Versions.SequenceEqual(["158.0", "157.0"]) && wDetail.Homepage == "" && wDetail.LicenseUrl == "https://x.example/l" && wDetail.UpdatedUtc?.Day == 19,
        "winstall app details: invalid versions dropped, non-http links dropped, date parsed");
    var (wPacks, _) = WinstallApiClient.ParsePackList("{\"data\":[{\"_id\":\"H9Sz\",\"name\":\"Windows Essentials\",\"appCount\":14,\"apps\":[{\"_id\":\"Google.Chrome\",\"name\":\"Google Chrome\"}]}]}");
    Check(wPacks.Count == 1 && wPacks[0].AppCount == 14 && wPacks[0].Apps.Count == 1, "winstall pack list: app count and preview apps");

    // ---------------- 5. Central Hub relay ----------------
    Console.WriteLine("[checks] Central Hub relay access control");
    await CheckHubAsync(Check);

    // ---------------- 6. Connect by computer name / IP healing ----------------
    Console.WriteLine("[checks] endpoint addressing, discovery replies and IP healing");
    EndpointChecks.Run(Check);

    // ---------------- 7. Signed agent announces ----------------
    Console.WriteLine("[checks] signed agent announces");
    AnnounceChecks.Run(Check);
    Console.WriteLine("[checks] Services view CollectionView refill");
    CollectionViewChecks.Run(Check);

    // ---------------- 8. Remote desktop quality presets ----------------
    Console.WriteLine("[checks] remote desktop quality presets");
    StreamPresetChecks.Run(Check);

    // ---------------- 9. Update All output parsing ----------------
    Console.WriteLine("[checks] Update All: winget and Linux package-manager output parsing");
    UpgradeChecks.Run(Check);
}
catch (Exception ex)
{
    failures.Add("unexpected exception: " + ex);
}
finally
{
    try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup */ }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("==== CONTROLLER CHECKS PASSED ====");
    return 0;
}
Console.WriteLine("==== CONTROLLER CHECKS FAILED ====");
foreach (var f in failures) Console.WriteLine("  - " + f);
return 1;

static string? TryDecode(string b64)
{
    try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
    catch { return null; }
}

static (int Exit, string Output) RunPowerShell(string work, string script)
{
    var file = Path.Combine(work, $"run-{Guid.NewGuid():N}.ps1");
    // Windows PowerShell can emit progress records (CLIXML) on stderr when it auto-loads a module,
    // which would corrupt checks that decode the combined output.
    File.WriteAllText(file, "$ProgressPreference = 'SilentlyContinue'\n" + script, new UTF8Encoding(true));
    var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    psi.Environment.Remove("PSModulePath"); // same as AgentlessService: don't inherit PS7's module path
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(60_000)) { p.Kill(true); return (-1, "timeout"); }
    return (p.ExitCode, stdout.Result + stderr.Result);
}

/// <summary>Parses every script with [Parser]::ParseFile in one PowerShell process; returns name -> first error.</summary>
static Dictionary<string, string> ParsePowerShell(string work, Dictionary<string, string> scripts)
{
    var map = new Dictionary<string, string>();
    var i = 0;
    foreach (var (name, body) in scripts)
    {
        var f = Path.Combine(work, $"parse-{i++}.ps1");
        File.WriteAllText(f, body, new UTF8Encoding(true));
        map[f] = name;
    }
    var listFile = Path.Combine(work, "files.txt");
    File.WriteAllLines(listFile, map.Keys);
    var runner = $$"""
        foreach ($f in Get-Content -LiteralPath '{{listFile}}') {
          $tokens = $null; $errors = $null
          [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$tokens, [ref]$errors)
          if ($errors.Count -gt 0) { "ERR`t$f`t$($errors[0].Message) (line $($errors[0].Extent.StartLineNumber))" }
        }
        "PARSE-DONE"
        """;
    var (_, output) = RunPowerShell(work, runner);
    var result = new Dictionary<string, string>();
    if (!output.Contains("PARSE-DONE"))
    {
        foreach (var name in scripts.Keys) result[name] = "parser did not run: " + output.Trim();
        return result;
    }
    foreach (var line in output.Split('\n'))
    {
        var parts = line.TrimEnd('\r').Split('\t');
        if (parts.Length == 3 && parts[0] == "ERR" && map.TryGetValue(parts[1], out var n)) result[n] = parts[2];
    }
    return result;
}

static async Task CheckHubAsync(Action<bool, string> check)
{
    var machines = new ObservableCollection<MachineProfile>
    {
        new() { Name = "<img src=x onerror=alert(1)>", Host = "127.0.0.1", Port = 1, StatusLabel = "Offline" },
    };
    var log = new List<string>();
    using var hub = new CentralHubService(() => machines, () => Task.CompletedTask, (c, m) => { lock (log) log.Add($"{c}: {m}"); });
    hub.Start(47480);
    check(hub.IsRunning, $"hub started on port {hub.Port}");
    if (!hub.IsRunning) return;

    var cookies = new CookieContainer();
    using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true }) { BaseAddress = new Uri($"http://localhost:{hub.Port}/") };
    async Task<HttpStatusCode> Status(HttpMethod method, string path, string? body = null, bool csrf = true)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (csrf) req.Headers.Add(CentralHubService.CsrfHeader, "1");
        using var res = await http.SendAsync(req);
        return res.StatusCode;
    }

    var page = await http.GetStringAsync("/");
    check(page.Contains("function esc(") && !page.Contains("JSON.stringify(e)"), "hub page escapes endpoint data and no longer inlines JSON into onclick");
    check(hub.AuthRequired, "sign-in is required by default");
    check(await Status(HttpMethod.Get, "api/endpoints") == HttpStatusCode.Unauthorized, "API without a session -> 401");
    check(await Status(HttpMethod.Get, "proxy/example.com/") == HttpStatusCode.Unauthorized, "reverse proxy without a session -> 401");
    check(await Status(HttpMethod.Post, "api/relay/127.0.0.1:1/shell", "{\"command\":\"hostname\"}") == HttpStatusCode.Unauthorized, "relay without a session -> 401");
    check(await Status(HttpMethod.Post, "api/auth/verify", $"{{\"code\":\"{hub.AccessKey}\"}}", csrf: false) == HttpStatusCode.Forbidden, "sign-in POST without the CSRF header -> 403");
    check(await Status(HttpMethod.Post, "api/auth/verify", $"{{\"code\":\"{hub.AccessKey?.ToLowerInvariant().Replace("-", " ")}\"}}") == HttpStatusCode.OK,
        "sign-in with the access key (any case / separators) -> 200");
    check(await Status(HttpMethod.Get, "api/endpoints") == HttpStatusCode.OK, "API with a session -> 200");
    check(await Status(HttpMethod.Post, "api/endpoints/scan", csrf: false) == HttpStatusCode.Forbidden, "state-changing POST without the CSRF header -> 403");
    check(await Status(HttpMethod.Get, "proxy/example.com/") == HttpStatusCode.NotFound, "reverse proxy to a host that is not a saved endpoint -> 404");
    check(await Status(HttpMethod.Get, "proxy/127.0.0.1/") == HttpStatusCode.NotFound, "reverse proxy to a saved endpoint without a web UI -> 404");
    check(await Status(HttpMethod.Post, "api/relay/10.255.255.1:7443/shell", "{\"command\":\"hostname\"}") == HttpStatusCode.NotFound, "relay to a host that is not a saved endpoint -> 404");
    check(await Status(HttpMethod.Post, "api/auth/verify", "not json") == HttpStatusCode.BadRequest, "malformed sign-in body -> 400 (no 500, not counted as a failed attempt)");
}
