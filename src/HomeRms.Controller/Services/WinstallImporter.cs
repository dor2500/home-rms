using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HomeRms.Shared.Server;

namespace HomeRms.Controller.Services;

/// <summary>Metadata a source supplied for an imported package (winstall packs carry name / version / publisher).</summary>
public sealed record ImportedPackageInfo(string Id, string? Name, string? Version, string? Publisher, string? Description);

public sealed class PackImportResult
{
    public string Format { get; set; } = "";
    public string? PackName { get; set; }
    /// <summary>Valid, de-duplicated IDs in source order.</summary>
    public List<string> Ids { get; } = [];
    /// <summary>Tokens that are not valid winget package IDs (shown to the user, never deployed).</summary>
    public List<string> Rejected { get; } = [];
    public Dictionary<string, ImportedPackageInfo> Info { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Add(string raw, ImportedPackageInfo? info = null)
    {
        var id = raw.Trim().Trim('"', '\'', '`');
        if (id.Length == 0) return;
        if (!WingetRules.IsValidPackageId(id))
        {
            var shown = id.Length > 60 ? id[..60] + "…" : id;
            if (!Rejected.Contains(shown)) Rejected.Add(shown);
            return;
        }
        if (Ids.Contains(id, StringComparer.OrdinalIgnoreCase)) return;
        Ids.Add(id);
        if (info is not null) Info[id] = info with { Id = id };
    }
}

/// <summary>
/// Parses winget/winstall pack formats into validated package IDs:
/// winstall.app pack URLs (via api.winstall.app/packs/{id}), winstall app URLs,
/// winstall-generated batch / PowerShell scripts, {"apps":[{"id"}]} JSON, winget export JSON
/// (Sources[].Packages[].PackageIdentifier) and plain ID lists.
/// </summary>
public static partial class WinstallImporter
{
    public const string PackApiBase = "https://api.winstall.app/packs/";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRMS-Controller/2.1");
        return http;
    }

    [GeneratedRegex(@"^\s*(?:https?://)?(?:www\.)?winstall\.app/packs/([A-Za-z0-9_-]{4,64})/?(?:[?#].*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PackUrlRegex();

    [GeneratedRegex(@"^\s*(?:https?://)?(?:www\.)?winstall\.app/apps/([^/?#\s]+)/?(?:[?#].*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AppUrlRegex();

    [GeneratedRegex(@"\bwinget(?:\.exe)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex WingetWordRegex();

    // The winget executable token itself: winget, winget.exe, or a full path to it.
    [GeneratedRegex(@"^(?:.*[\\/])?winget(?:\.exe)?$", RegexOptions.IgnoreCase)]
    private static partial Regex WingetTokenRegex();

    /// <summary>Returns the pack id when <paramref name="text"/> is a single winstall pack URL.</summary>
    public static bool TryGetPackId(string text, out string packId)
    {
        var m = PackUrlRegex().Match(text);
        packId = m.Success ? m.Groups[1].Value : "";
        return m.Success;
    }

    /// <summary>Downloads a winstall pack through the public winstall API (the pack page itself is rendered client-side).</summary>
    public static async Task<PackImportResult> FetchPackAsync(string packId, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(PackApiBase + Uri.EscapeDataString(packId), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("winstall says this pack does not exist or is private. Open the pack on winstall.app, use \"Generate script\" and paste the script here instead.");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"winstall API returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. Paste the pack's generated winget script instead.");

        var json = await resp.Content.ReadAsStringAsync(ct);
        var result = ParsePackJson(json);
        if (result.Ids.Count == 0 && result.Rejected.Count == 0)
            throw new InvalidDataException("The winstall pack response contained no apps (API format may have changed). Paste the pack's generated winget script instead.");
        return result;
    }

    /// <summary>Parses the api.winstall.app/packs/{id} response: { name, apps: [ { _id, name, appVersion, publisher, desc } ] }.</summary>
    public static PackImportResult ParsePackJson(string json)
    {
        var result = new PackImportResult { Format = "winstall pack" };
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return result;
        result.PackName = Str(root, "name");
        if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in apps.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String) { result.Add(a.GetString()!); continue; }
                var id = Str(a, "_id") ?? Str(a, "id") ?? Str(a, "PackageIdentifier");
                if (id is null) continue;
                result.Add(id, new ImportedPackageInfo(id, Str(a, "name"), Str(a, "appVersion") ?? Str(a, "version"),
                    Str(a, "publisher"), Str(a, "desc") ?? Str(a, "description")));
            }
        }
        return result;
    }

    /// <summary>Parses pasted text (anything except a pack URL, which needs <see cref="FetchPackAsync"/>).</summary>
    public static PackImportResult ParseText(string text)
    {
        text = text.Trim().TrimStart('﻿');
        if (text.Length == 0) return new PackImportResult { Format = "empty" };

        if (text[0] is '{' or '[')
        {
            var json = TryParseJson(text);
            if (json is not null) return json;
        }

        if (WingetWordRegex().IsMatch(text))
            return ParseScript(text);

        return ParseList(text);
    }

    private static PackImportResult? TryParseJson(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            var result = new PackImportResult();

            if (root.ValueKind == JsonValueKind.Array)
            {
                result.Format = "JSON array";
                foreach (var e in root.EnumerateArray()) AddJsonApp(result, e);
                return result;
            }
            if (root.ValueKind != JsonValueKind.Object) return result;

            // winget export: { "Sources": [ { "Packages": [ { "PackageIdentifier": "..." } ] } ] }
            if (root.TryGetProperty("Sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                result.Format = "winget export JSON";
                foreach (var s in sources.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("Packages", out var pkgs) && pkgs.ValueKind == JsonValueKind.Array)
                        foreach (var p in pkgs.EnumerateArray()) AddJsonApp(result, p);
                return result;
            }

            // winstall / Home RMS: { "name": "...", "apps": [ { "id" | "_id": "..." } ] }
            if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
            {
                result.Format = "apps JSON";
                result.PackName = Str(root, "name");
                foreach (var a in apps.EnumerateArray()) AddJsonApp(result, a);
                return result;
            }

            // Home RMS pack format: { "Name": "...", "PackageIds": [ ... ] }
            if (root.TryGetProperty("PackageIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                result.Format = "Home RMS pack JSON";
                result.PackName = Str(root, "Name");
                foreach (var a in ids.EnumerateArray()) AddJsonApp(result, a);
                return result;
            }

            result.Format = "unrecognised JSON";
            return result;
        }
    }

    private static void AddJsonApp(PackImportResult result, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) { result.Add(e.GetString()!); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        var id = Str(e, "PackageIdentifier") ?? Str(e, "id") ?? Str(e, "Id") ?? Str(e, "_id");
        if (id is null) return;
        result.Add(id, new ImportedPackageInfo(id, Str(e, "name") ?? Str(e, "Name"),
            Str(e, "Version") ?? Str(e, "appVersion") ?? Str(e, "version"), Str(e, "publisher"), null));
    }

    private static string? Str(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // winget options that consume the following token as their value.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--id", "--name", "--moniker", "-v", "--version", "-s", "--source", "--scope", "-a", "--architecture",
        "--locale", "--override", "--custom", "-l", "--location", "-o", "--log", "--header", "--installer-type",
        "-m", "--manifest", "--authentication-mode", "--authentication-account", "--rename", "--product-code",
        "-q", "--query",
    };

    private static readonly HashSet<string> InstallVerbs = new(StringComparer.OrdinalIgnoreCase) { "install", "add", "upgrade", "update" };

    /// <summary>Extracts IDs from winget command lines (batch "&amp;&amp;"-chains, PowerShell ";"-chains, one per line, ...).</summary>
    public static PackImportResult ParseScript(string text)
    {
        var result = new PackImportResult { Format = "winget script" };
        foreach (var command in SplitCommands(text))
        {
            var tokens = Tokenize(command);
            var w = tokens.FindIndex(t => WingetTokenRegex().IsMatch(t));
            if (w < 0 || w + 1 >= tokens.Count || !InstallVerbs.Contains(tokens[w + 1])) continue;

            string? id = null, positional = null, nameOnly = null;
            for (var i = w + 2; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.StartsWith('-'))
                {
                    var eq = t.IndexOf('=');
                    var opt = eq > 0 ? t[..eq] : t;
                    string? val = eq > 0 ? t[(eq + 1)..] : null;
                    if (!ValueOptions.Contains(opt)) continue;           // plain flag: -e, --silent, --accept-*, -h ...
                    if (val is null && i + 1 < tokens.Count) val = tokens[++i];
                    if (opt.Equals("--id", StringComparison.OrdinalIgnoreCase)) id = val;
                    else if (opt is "-q" or "--query" && positional is null) positional = val;
                    else if (opt.Equals("--name", StringComparison.OrdinalIgnoreCase)) nameOnly = val;
                    continue;
                }
                positional ??= t;
            }

            var chosen = id ?? positional;
            if (chosen is not null) result.Add(chosen);
            else if (nameOnly is not null) result.Rejected.Add($"--name {nameOnly} (use --id)");
        }
        return result;
    }

    /// <summary>One ID (or winstall app URL) per token; '#', '//' and 'REM' lines are comments.</summary>
    public static PackImportResult ParseList(string text)
    {
        var result = new PackImportResult { Format = "ID list" };
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//") ||
                line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::"))
                continue;

            foreach (var token in line.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var app = AppUrlRegex().Match(token);
                if (app.Success) { result.Add(Uri.UnescapeDataString(app.Groups[1].Value)); continue; }
                if (PackUrlRegex().IsMatch(token)) { result.Rejected.Add($"{token} (import a pack URL on its own)"); continue; }
                result.Add(token);
            }
        }
        return result;
    }

    private static IEnumerable<string> SplitCommands(string text)
    {
        // Line continuations: PowerShell backtick and batch caret.
        text = text.Replace("`\r\n", " ").Replace("`\n", " ").Replace("^\r\n", " ").Replace("^\n", " ");
        var sb = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                sb.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            if (c is '\n' or '\r' or ';' or '&' or '|')
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        var inToken = false;
        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else sb.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; inToken = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (inToken) { tokens.Add(sb.ToString()); sb.Clear(); inToken = false; }
                continue;
            }
            sb.Append(c);
            inToken = true;
        }
        if (inToken) tokens.Add(sb.ToString());
        return tokens;
    }
}
