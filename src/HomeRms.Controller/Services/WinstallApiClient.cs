using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HomeRms.Shared.Server;

namespace HomeRms.Controller.Services;

/// <summary>An app as listed by api.winstall.app (lists, search, categories, trending).</summary>
public sealed record WinstallApp(string Id, string Name, string Publisher, string Description, string LatestVersion,
    IReadOnlyList<string> Categories, IReadOnlyList<string> Tags);

/// <summary>Full app details from api.winstall.app/apps/{id}.</summary>
public sealed record WinstallAppDetails(string Id, string Name, string Publisher, string Description, string FullDescription,
    string LatestVersion, IReadOnlyList<string> Versions, IReadOnlyList<string> Tags, string License, string LicenseUrl,
    string Homepage, string Copyright, string Moniker, DateTime? UpdatedUtc);

public sealed record WinstallPackApp(string Id, string Name, string Publisher, string? Version);

public sealed record WinstallPack(string Id, string Name, string Description, IReadOnlyList<WinstallPackApp> Apps, int AppCount);

/// <summary>One page of results plus where it came from (fresh network, fresh cache or stale cache).</summary>
public sealed record WinstallPage<T>(IReadOnlyList<T> Items, int Total, bool Stale);

/// <summary>
/// Read-only client for the public winstall API (https://api.winstall.app), the same endpoints winstall.app
/// itself calls: <c>/apps?offset&amp;limit</c>, <c>/apps/search?q</c>, <c>/apps/categories/{slug}</c>,
/// <c>/apps/trending</c>, <c>/apps/{id}</c>, <c>/packs/trending</c> and <c>/packs?offset&amp;limit</c>.
/// Every response is cached under %LOCALAPPDATA%\HomeRMS\winstall-cache with a per-endpoint TTL; when the
/// network fails a stale cached copy is returned (flagged <see cref="WinstallPage{T}.Stale"/>), otherwise the
/// call throws and the caller falls back to the official index / built-in list.
/// </summary>
public sealed class WinstallApiClient
{
    public const string ApiBase = "https://api.winstall.app";

    public static readonly TimeSpan TrendingTtl = TimeSpan.FromHours(6);
    public static readonly TimeSpan ListTtl = TimeSpan.FromHours(12);
    public static readonly TimeSpan DetailsTtl = TimeSpan.FromHours(24);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRMS", "winstall-cache");

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>Last network error (null after a successful request).</summary>
    public string? LastError { get; private set; }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRMS-Controller/2.1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    // ---------------- Apps ----------------
    public Task<WinstallPage<WinstallApp>> ListAppsAsync(int offset, int limit, CancellationToken ct = default) =>
        GetAppPageAsync($"/apps?offset={offset}&limit={limit}", ListTtl, ct);

    public Task<WinstallPage<WinstallApp>> SearchAppsAsync(string query, int offset, int limit, CancellationToken ct = default) =>
        GetAppPageAsync($"/apps/search?q={Uri.EscapeDataString(query.Trim())}&offset={offset}&limit={limit}", TrendingTtl, ct);

    public Task<WinstallPage<WinstallApp>> CategoryAppsAsync(string slug, int offset, int limit, CancellationToken ct = default) =>
        GetAppPageAsync($"/apps/categories/{Uri.EscapeDataString(slug)}?offset={offset}&limit={limit}", ListTtl, ct);

    public Task<WinstallPage<WinstallApp>> TrendingAppsAsync(CancellationToken ct = default) =>
        GetAppPageAsync("/apps/trending", TrendingTtl, ct);

    public async Task<(WinstallAppDetails Details, bool Stale)> GetAppAsync(string id, CancellationToken ct = default)
    {
        if (!WingetRules.IsValidPackageId(id)) throw new ArgumentException($"Invalid package id '{id}'.", nameof(id));
        var (json, stale) = await GetJsonAsync($"/apps/{Uri.EscapeDataString(id)}", DetailsTtl, ct);
        return (ParseAppDetails(json), stale);
    }

    // ---------------- Packs ----------------
    public async Task<WinstallPage<WinstallPack>> TrendingPacksAsync(CancellationToken ct = default)
    {
        var (json, stale) = await GetJsonAsync("/packs/trending", TrendingTtl, ct);
        var (items, total) = ParsePackList(json);
        return new WinstallPage<WinstallPack>(items, total, stale);
    }

    public async Task<WinstallPage<WinstallPack>> ListPacksAsync(int offset, int limit, CancellationToken ct = default)
    {
        var (json, stale) = await GetJsonAsync($"/packs?offset={offset}&limit={limit}", ListTtl, ct);
        var (items, total) = ParsePackList(json);
        return new WinstallPage<WinstallPack>(items, total, stale);
    }

    // ---------------- Parsing (public for offline checks) ----------------
    public static (List<WinstallApp> Items, int Total) ParseAppList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d : default;
        var items = new List<WinstallApp>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in data.EnumerateArray())
            {
                var id = Str(a, "_id") ?? Str(a, "id");
                if (!WingetRules.IsValidPackageId(id)) continue;
                items.Add(new WinstallApp(id!, Str(a, "name") ?? id!, Str(a, "publisher") ?? WingetCatalogService.PublisherFromId(id!),
                    Clean(Str(a, "desc")), Str(a, "latestVersion") ?? "", StrList(a, "categories"), StrList(a, "tags")));
            }
        }
        var total = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : items.Count;
        return (items, total);
    }

    public static WinstallAppDetails ParseAppDetails(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var a = doc.RootElement;
        var id = Str(a, "_id") ?? Str(a, "id") ?? throw new InvalidDataException("winstall app response has no id.");
        var versions = new List<string>();
        if (a.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vs.EnumerateArray())
            {
                var ver = v.ValueKind == JsonValueKind.String ? v.GetString() : Str(v, "version");
                if (WingetRules.IsValidVersion(ver) && !versions.Contains(ver!)) versions.Add(ver!);
            }
        }
        var latest = Str(a, "latestVersion") ?? versions.FirstOrDefault() ?? "";
        if (WingetRules.IsValidVersion(latest) && !versions.Contains(latest)) versions.Insert(0, latest);

        DateTime? updated = DateTime.TryParse(Str(a, "updatedAt"), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var u) ? u : null;
        return new WinstallAppDetails(id, Str(a, "name") ?? id, Str(a, "publisher") ?? Str(a, "author") ?? WingetCatalogService.PublisherFromId(id),
            Clean(Str(a, "desc")), Clean(Str(a, "fullDesc")), latest, versions, StrList(a, "tags"),
            Str(a, "license") ?? "", SafeUrl(Str(a, "licenseUrl")), SafeUrl(Str(a, "homepage")), Clean(Str(a, "copyright")),
            Str(a, "moniker") ?? "", updated);
    }

    public static (List<WinstallPack> Items, int Total) ParsePackList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d : default;
        var items = new List<WinstallPack>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in data.EnumerateArray())
            {
                var id = Str(p, "_id");
                var name = Str(p, "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                var apps = new List<WinstallPackApp>();
                if (p.TryGetProperty("apps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in arr.EnumerateArray())
                    {
                        var appId = a.ValueKind == JsonValueKind.String ? a.GetString() : Str(a, "_id") ?? Str(a, "id");
                        if (!WingetRules.IsValidPackageId(appId)) continue;
                        apps.Add(new WinstallPackApp(appId!, Str(a, "name") ?? appId!, Str(a, "publisher") ?? "", Str(a, "appVersion")));
                    }
                }
                var count = p.TryGetProperty("appCount", out var c) && c.TryGetInt32(out var cn) ? Math.Max(cn, apps.Count) : apps.Count;
                items.Add(new WinstallPack(id!, Clean(name), Clean(Str(p, "description")), apps, count));
            }
        }
        var total = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : items.Count;
        return (items, total);
    }

    private async Task<WinstallPage<WinstallApp>> GetAppPageAsync(string path, TimeSpan ttl, CancellationToken ct)
    {
        var (json, stale) = await GetJsonAsync(path, ttl, ct);
        var (items, total) = ParseAppList(json);
        return new WinstallPage<WinstallApp>(items, total, stale);
    }

    // ---------------- Cache ----------------
    /// <summary>Fresh cache, else network (cached on success), else stale cache, else throws.</summary>
    private async Task<(string Json, bool Stale)> GetJsonAsync(string path, TimeSpan ttl, CancellationToken ct)
    {
        var file = CachePath(path);
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < ttl)
        {
            try { return (await File.ReadAllTextAsync(file, ct), false); }
            catch (IOException) { /* fall through to the network */ }
        }

        try
        {
            using var resp = await Http.GetAsync(ApiBase + path, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"api.winstall.app returned HTTP {(int)resp.StatusCode} for {path.Split('?')[0]}");
            var json = await resp.Content.ReadAsStringAsync(ct);
            using (JsonDocument.Parse(json)) { } // only cache valid JSON
            try
            {
                Directory.CreateDirectory(CacheDir);
                HomeRms.Shared.AtomicFile.WriteAllText(file, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            LastError = null;
            return (json, false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LastError = ex is TaskCanceledException ? "api.winstall.app timed out" : ex.Message;
            if (File.Exists(file))
            {
                try { return (await File.ReadAllTextAsync(file, ct), true); }
                catch (IOException) { }
            }
            throw;
        }
    }

    private static string CachePath(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24].ToLowerInvariant();
        return Path.Combine(CacheDir, hash + ".json");
    }

    // ---------------- Helpers ----------------
    private static string? Str(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> StrList(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => Clean(e.GetString())).Where(s => s.Length > 0).Distinct().ToList()
            : [];

    /// <summary>Strips control characters and collapses whitespace (API text is shown verbatim).</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Only http(s) URLs are ever shown as links.</summary>
    private static string SafeUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : "";
}

/// <summary>winstall's category slugs (as used by /apps/categories/{slug}) with labels and offline index tags.</summary>
public sealed record DeployCategory(string Slug, string Label, string Tone, string IconKey, IReadOnlyCollection<string> FallbackTags)
{
    public static IReadOnlyList<DeployCategory> All { get; } =
    [
        new("browser", "Browsers", "Browser", "DGeoGlobe", ["browser", "web-browser", "chromium", "firefox"]),
        new("development", "Developer Tools", "Dev", "DGeoCode", ["developer-tools", "development", "programming", "ide", "sdk", "git", "compiler", "devops"]),
        new("documents", "Office", "Docs", "DGeoDoc", ["office", "pdf", "document", "documents", "word-processor", "spreadsheet"]),
        new("communication", "Communication", "Comms", "DGeoChat", ["chat", "messaging", "messenger", "communication", "email", "voip", "video-conferencing"]),
        new("utilities", "Utilities", "Util", "DGeoWrench", ["utility", "utilities", "tool", "tools", "file-manager", "compression", "archive", "zip"]),
        new("security", "Security", "Dev", "DGeoShield", ["security", "password", "password-manager", "encryption", "antivirus", "privacy"]),
        new("productivity", "Productivity", "Docs", "DGeoDoc", ["productivity", "notes", "note-taking", "markdown", "calendar", "todo"]),
        new("cloud_storage", "Cloud Storage", "Comms", "DGeoGlobe", ["cloud", "cloud-storage", "sync", "backup"]),
        new("collaboration", "Collaboration", "Comms", "DGeoChat", ["collaboration", "meeting", "team", "whiteboard"]),
        new("entertainment", "Entertainment", "Browser", "DGeoPlay", ["video", "audio", "media", "music", "player", "streaming"]),
        new("photo", "Photos", "Util", "DGeoImage", ["image", "photo", "graphics", "image-editor", "drawing"]),
        new("game", "Games", "Browser", "DGeoPlay", ["game", "games", "gaming", "emulator", "launcher"]),
        new("screenshots", "Screenshots", "Util", "DGeoImage", ["screenshot", "screen-capture", "screen-recording"]),
        new("runtimes", "Runtimes", "Dev", "DGeoCode", ["runtime", "framework", "redistributable", "vcredist", ".net"]),
    ];

    public static IReadOnlyList<DeployCategory> Top { get; } = All.Take(5).ToList();

    public static DeployCategory? Find(string? slug) => All.FirstOrDefault(c => c.Slug == slug);
}
