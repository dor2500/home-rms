using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HomeRms.Controller.Models;
using HomeRms.Controller.Services;

/// <summary>
/// Central Hub web console: static SPA (CSP, no inline script), /api/v2 access control (sign-in, CSRF
/// header, saved endpoints only), path validation and transfer limits. Runs on a test port with in-memory
/// relay settings (the user's hub-relay.json is never read or written). No agent is contacted: every
/// request below is answered (or rejected) before the hub would dial an endpoint.
/// </summary>
internal static class HubWebChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        PureChecks(check);

        var machines = new ObservableCollection<MachineProfile>
        {
            new() { Name = "<img src=x onerror=alert(1)>", Host = "127.0.0.1", Port = 1, Kind = EndpointKind.WindowsAgent, StatusLabel = "Offline" },
            new() { Name = "nas", Host = "127.0.0.2", Port = 1, Kind = EndpointKind.Linux, ConnectionMode = ConnectionMode.Agent },
        };
        var log = new List<string>();
        using var hub = new CentralHubService(() => machines, () => Task.CompletedTask, (c, m) => { lock (log) log.Add($"{c}: {m}"); },
            settings: HubRelaySettings.CreateEphemeral(requireAuth: true, allowLan: false));
        hub.Start(47490);
        check(hub.IsRunning && !hub.LanBound, $"web console hub started on localhost:{hub.Port}");
        if (!hub.IsRunning) return;

        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = false })
        { BaseAddress = new Uri($"http://localhost:{hub.Port}/") };

        async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Res)> Send(HttpMethod method, string path, string? body = null, bool csrf = true)
        {
            using var req = new HttpRequestMessage(method, path);
            if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (csrf) req.Headers.Add(CentralHubService.CsrfHeader, "1");
            var res = await http.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync(), res);
        }
        static string? Code(string body)
        {
            try { return JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null; }
            catch (JsonException) { return null; }
        }

        // ---- static app ----
        var (pageStatus, page, pageRes) = await Send(HttpMethod.Get, "/", csrf: false);
        var csp = pageRes.Headers.TryGetValues("Content-Security-Policy", out var v) ? string.Join(";", v) : "";
        check(pageStatus == HttpStatusCode.OK && page.Contains("<script src=\"/app.js\"") && !page.Contains("<script>") && !page.Contains("onclick="),
            "SPA page is served without inline script or inline handlers");
        check(csp.Contains("script-src 'self'") && csp.Contains("frame-ancestors 'none'") && !csp.Contains("unsafe-inline") && !csp.Contains("http"),
            "SPA has a strict Content-Security-Policy (self only, no unsafe-inline, no external origins)");
        check(pageRes.Content.Headers.ContentType?.MediaType == "text/html", "index.html content type");
        var (jsStatus, js, jsRes) = await Send(HttpMethod.Get, "/app.js", csrf: false);
        check(jsStatus == HttpStatusCode.OK && jsRes.Content.Headers.ContentType?.MediaType == "text/javascript" && !js.Contains("innerHTML") && !js.Contains("eval("),
            "app.js: JavaScript content type, never uses innerHTML or eval");
        var (cssStatus, _, cssRes) = await Send(HttpMethod.Get, "/app.css", csrf: false);
        check(cssStatus == HttpStatusCode.OK && cssRes.Content.Headers.ContentType?.MediaType == "text/css", "app.css content type");
        var (mobStatus, _, mobRes) = await Send(HttpMethod.Get, "/mobile", csrf: false);
        check(mobStatus == HttpStatusCode.Found && mobRes.Headers.Location?.OriginalString == "/", "old /mobile page redirects to the web console");

        // ---- sign-in gate ----
        check((await Send(HttpMethod.Get, "api/v2/endpoints", csrf: false)).Status == HttpStatusCode.Unauthorized, "v2 API without a session -> 401");
        check((await Send(HttpMethod.Get, "api/v2/overview", csrf: false)).Status == HttpStatusCode.Unauthorized, "v2 overview without a session -> 401");
        check((await Send(HttpMethod.Post, "api/v2/endpoints-scan", "{}")).Status == HttpStatusCode.Unauthorized, "v2 POST without a session -> 401");
        check((await Send(HttpMethod.Post, "api/auth/verify", $"{{\"code\":\"{hub.AccessKey}\"}}")).Status == HttpStatusCode.OK, "sign-in with the access key");

        // ---- with a session ----
        var (epStatus, epBody, _) = await Send(HttpMethod.Get, "api/v2/endpoints", csrf: false);
        check(epStatus == HttpStatusCode.OK, "v2 endpoints with a session -> 200");
        check(!epBody.Contains("<img") && epBody.Contains("\\u003Cimg"), "endpoint names are HTML-escaped in the JSON (\\u003C), never raw markup");
        string winId = "", linuxId = "";
        using (var doc = JsonDocument.Parse(epBody))
        {
            var list = doc.RootElement.GetProperty("endpoints").EnumerateArray().ToList();
            winId = list[0].GetProperty("id").GetString()!;
            linuxId = list[1].GetProperty("id").GetString()!;
            check(list[1].GetProperty("isLinux").GetBoolean() && list[1].GetProperty("caps").GetProperty("shellName").GetString() == "bash"
                  && !list[1].GetProperty("caps").GetProperty("deploy").GetBoolean() && list[0].GetProperty("caps").GetProperty("deploy").GetBoolean(),
                "endpoint capabilities: Linux agent = bash, no winget deploy; Windows agent can deploy");
            check(winId.Length == 12 && winId.All(Uri.IsHexDigit), "endpoint ids are opaque hex");
        }
        check((await Send(HttpMethod.Get, "api/v2/overview", csrf: false)).Status == HttpStatusCode.OK, "v2 overview with a session -> 200");

        var (unk, unkBody, _) = await Send(HttpMethod.Get, "api/v2/endpoints/ffffffffffff", csrf: false);
        check(unk == HttpStatusCode.NotFound && Code(unkBody) == "unknown_endpoint", "unknown endpoint -> 404 unknown_endpoint");
        var (unk2, unk2Body, _) = await Send(HttpMethod.Post, "api/v2/endpoints/ffffffffffff/shell", "{\"command\":\"hostname\"}");
        check(unk2 == HttpStatusCode.NotFound && Code(unk2Body) == "unknown_endpoint", "shell on an unknown endpoint -> 404 (no open relay)");
        check((await Send(HttpMethod.Get, "api/v2/nope", csrf: false)).Status == HttpStatusCode.NotFound, "unknown v2 route -> 404");

        // ---- CSRF ----
        check((await Send(HttpMethod.Post, "api/v2/endpoints-scan", "{}", csrf: false)).Status == HttpStatusCode.Forbidden, "mutating v2 call without the CSRF header -> 403");
        check((await Send(HttpMethod.Post, $"api/v2/endpoints/{winId}/power", "{\"action\":\"restart\"}", csrf: false)).Status == HttpStatusCode.Forbidden, "power without the CSRF header -> 403");
        check((await Send(HttpMethod.Post, $"api/v2/endpoints/{winId}/files/delete", "{\"path\":\"C:\\\\x\"}", csrf: false)).Status == HttpStatusCode.Forbidden, "file delete without the CSRF header -> 403");
        check((await Send(HttpMethod.Post, "api/v2/endpoints-scan", "{}")).Status == HttpStatusCode.OK, "mutating v2 call with the CSRF header -> 200");
        lock (log) check(log.Any(l => l.StartsWith("WEB: [web:") && l.Contains("LAN scan")), "mutating calls are logged with the browser session as operator");

        // ---- path traversal / odd paths are rejected before anything is relayed ----
        string[] badWin = ["C:\\Windows\\..\\x", "C:\\a\\.\\b", "\\\\server\\share", "\\\\?\\C:\\x", "C:\\x:ads", "relative\\path", "C:x", "C:\\a\\\\b"];
        foreach (var p in badWin)
        {
            var (st, b, _) = await Send(HttpMethod.Get, $"api/v2/endpoints/{winId}/files/list?path={Uri.EscapeDataString(p)}", csrf: false);
            check(st == HttpStatusCode.BadRequest && Code(b) == "invalid_path", $"Windows path rejected: {p}");
        }
        foreach (var p in new[] { "/etc/../shadow", "etc/passwd", "/home/./x", "/a//b", "/x\n" })
        {
            var (st, b, _) = await Send(HttpMethod.Get, $"api/v2/endpoints/{linuxId}/files/download?path={Uri.EscapeDataString(p)}", csrf: false);
            check(st == HttpStatusCode.BadRequest && Code(b) == "invalid_path", $"Linux path rejected: {p.Replace("\n", "\\n")}");
        }
        foreach (var n in new[] { "../evil", "..", "a/b", "a\\b" })
        {
            var (st, b, _) = await Send(HttpMethod.Post, $"api/v2/endpoints/{linuxId}/files/upload?dir=%2Ftmp&name={Uri.EscapeDataString(n)}", "x");
            check(st == HttpStatusCode.BadRequest && Code(b) == "invalid_path", $"upload file name rejected: {n}");
        }
        var (rn, rnBody, _) = await Send(HttpMethod.Post, $"api/v2/endpoints/{winId}/files/rename", "{\"path\":\"C:\\\\temp\\\\a.txt\",\"newName\":\"..\\\\..\\\\Windows\\\\a.txt\"}");
        check(rn == HttpStatusCode.BadRequest && Code(rnBody) == "invalid_path", "rename to a name with separators / traversal rejected");
        var (mk, mkBody, _) = await Send(HttpMethod.Post, $"api/v2/endpoints/{winId}/files/mkdir", "{\"dir\":\"C:\\\\temp\",\"name\":\"a:b\"}");
        check(mk == HttpStatusCode.BadRequest && Code(mkBody) == "invalid_path", "new folder name with ':' rejected on Windows");

        // ---- other validation ----
        var (dep, depBody, _) = await Send(HttpMethod.Post, "api/v2/deploy", $"{{\"packages\":[\"Mozilla.Firefox\"],\"endpoints\":[\"{linuxId}\"]}}");
        check(dep == HttpStatusCode.BadRequest && Code(depBody) == "windows_only", "winget deploy to a Linux endpoint -> 400 windows_only");
        var (dep2, dep2Body, _) = await Send(HttpMethod.Post, "api/v2/deploy", $"{{\"packages\":[\"bad id; calc\"],\"endpoints\":[\"{winId}\"]}}");
        check(dep2 == HttpStatusCode.BadRequest && Code(dep2Body) == "invalid_package", "invalid winget package id -> 400");
        var (pw, pwBody, _) = await Send(HttpMethod.Post, $"api/v2/endpoints/{winId}/power", "{\"action\":\"format-disk\"}");
        check(pw == HttpStatusCode.BadRequest && Code(pwBody) == "invalid_action", "unknown power action -> 400");
        var (ua, uaBody, _) = await Send(HttpMethod.Post, $"api/v2/endpoints/{winId}/upgrade-all", "{}");
        check(ua == HttpStatusCode.NotImplemented && Code(uaBody) == "not_implemented", "Update All hook not wired yet -> 501 (TODO hook)");
        var (ws, wsBody, _) = await Send(HttpMethod.Get, "api/v2/winget/search?q=firefox", csrf: false);
        check(ws == HttpStatusCode.OK && wsBody.Contains("\"available\":false"), "winget search without a catalog answers 'not available' instead of failing");

        // ---- sign out ends the session ----
        check((await Send(HttpMethod.Post, "api/auth/logout", "{}")).Status == HttpStatusCode.OK, "sign out");
        check((await Send(HttpMethod.Get, "api/v2/endpoints", csrf: false)).Status == HttpStatusCode.Unauthorized, "after sign-out the session is gone -> 401");
    }

    private static void PureChecks(Action<bool, string> check)
    {
        check(WebPaths.ValidatePath("C:/Users/x", linux: false) == "C:\\Users\\x" && WebPaths.ValidatePath("C:\\", false) == "C:\\", "Windows paths normalized (/ -> \\, root kept)");
        check(WebPaths.ValidatePath("/", linux: true) == "/" && WebPaths.ValidatePath("/home/a/", true) == "/home/a", "Linux paths: root kept, trailing slash trimmed");
        check(WebPaths.ParentOf("/home/a", true) == "/home" && WebPaths.ParentOf("/home", true) == "/" && WebPaths.ParentOf("C:\\a\\b", false) == "C:\\a" && WebPaths.ParentOf("C:\\a", false) == "C:\\",
            "parent paths for rename");
        check(WebPaths.Combine("/", "x", true) == "/x" && WebPaths.Combine("C:\\", "x", false) == "C:\\x" && WebPaths.Combine("C:\\a", "x", false) == "C:\\a\\x", "path combine");
        var cd = WebPaths.ContentDisposition("rep\"ort;\r\n€.txt");
        check(cd.StartsWith("attachment; filename=\"rep_ort___") && !cd.Contains('\r') && !cd.Contains('\n') && cd.Contains("filename*=UTF-8''rep%22ort%3B%0D%0A%E2%82%AC.txt"),
            "download Content-Disposition is header-safe (quotes, CR/LF, unicode)");

        var limiter = new TransferLimiter(perSession: 2, total: 3, startsPerMinute: 4);
        var a1 = limiter.TryAcquire("a", out _);
        var a2 = limiter.TryAcquire("a", out _);
        var a3 = limiter.TryAcquire("a", out var whyA);
        var b1 = limiter.TryAcquire("b", out _);
        var c1 = limiter.TryAcquire("c", out var whyTotal);
        check(a1 is not null && a2 is not null && a3 is null && whyA.Contains("per browser session") && b1 is not null && c1 is null && whyTotal.Contains("busy"),
            "transfer limiter: 2 per session, 3 in total");
        a1!.Dispose(); a1.Dispose(); // double dispose must not free two slots
        var a4 = limiter.TryAcquire("a", out _);
        var a5 = limiter.TryAcquire("a", out _);
        check(a4 is not null && a5 is null, "transfer limiter: a released slot can be reused (double dispose frees one)");
        a2!.Dispose(); a4!.Dispose(); b1!.Dispose();
        var a6 = limiter.TryAcquire("a", out _); // 4th start this minute (a1, a2, a4, a6)
        a6?.Dispose();
        var a7 = limiter.TryAcquire("a", out var whyRate2);
        check(a6 is not null && a7 is null && whyRate2.Contains("per minute"), $"transfer limiter: at most 4 starts per minute per session ({whyRate2})");
    }
}
