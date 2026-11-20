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

    var parseErrors = ParsePowerShell(work, scripts);
    foreach (var name in scripts.Keys)
        Check(!parseErrors.TryGetValue(name, out var err), $"parses: {name}{(parseErrors.TryGetValue(name, out var e2) ? " -> " + e2 : "")}");

    // ---------------- 2. Credential round trip ----------------
    Console.WriteLine("[checks] WinRM credential encoding");
    var prelude = AgentlessService.BuildCredentialPrelude("DOMAIN\\o'brien", hostile) +
                  "\n[Console]::Out.Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($cred.UserName + '|' + $cred.GetNetworkCredential().Password)))";
    var (_, credOut) = RunPowerShell(work, prelude);
    var decoded = TryDecode(credOut.Trim());
    Check(decoded == "DOMAIN\\o'brien|" + hostile, "user name and password with quotes / '@ / $ / backticks / newlines round-trip unchanged");

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

    // ---------------- 5. Central Hub relay ----------------
    Console.WriteLine("[checks] Central Hub relay access control");
    await CheckHubAsync(Check);
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
    File.WriteAllText(file, script, new UTF8Encoding(true));
    var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
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
