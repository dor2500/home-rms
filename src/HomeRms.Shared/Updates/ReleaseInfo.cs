using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomeRms.Shared.Updates;

/// <summary>Release-tag parsing and version comparison used by the self-updaters.</summary>
public static class ReleaseVersion
{
    private static readonly Regex StableTag = new(@"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a stable release tag such as "v2.2.0" or "2.2". Pre-release / build-metadata tags
    /// ("v2.3.0-beta.1", "v2.3.0+abc") are rejected so they are never offered as updates.
    /// </summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var m = StableTag.Match(tag.Trim());
        if (!m.Success) return false;
        version = new Version(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        return true;
    }

    /// <summary>Drops the revision (and normalises missing parts) so 2.2.0.0 == 2.2.0 == 2.2.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>True when <paramref name="remote"/> is strictly newer than <paramref name="current"/> (major.minor.patch only).</summary>
    public static bool IsNewer(Version remote, Version current) => Normalize(remote) > Normalize(current);

    public static string Display(Version v)
    {
        var n = Normalize(v);
        return $"v{n.Major}.{n.Minor}.{n.Build}";
    }
}

/// <summary>The parts of a GitHub "latest release" response the updaters care about.</summary>
public sealed record ReleaseInfo(
    string Tag,
    Version? Version,
    bool IsPrerelease,
    string Body,
    DateTimeOffset? PublishedAt,
    string? AssetUrl,
    long AssetSize,
    string? ChecksumUrl)
{
    /// <summary>Parses the JSON of GET /repos/{owner}/{repo}/releases/latest and picks <paramref name="assetName"/>.</summary>
    public static ReleaseInfo Parse(string json, string assetName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Str(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

        var tag = Str("tag_name");
        var prerelease = root.TryGetProperty("prerelease", out var preEl) && preEl.ValueKind == JsonValueKind.True;
        var draft = root.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.True;
        Version? version = !prerelease && !draft && ReleaseVersion.TryParseTag(tag, out var v) ? v : null;
        DateTimeOffset? published = DateTimeOffset.TryParse(Str("published_at"), out var p) ? p : null;

        string? assetUrl = null, checksumUrl = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = url;
                    assetSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                }
                else if (string.Equals(name, "checksums.sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        return new ReleaseInfo(tag, version, prerelease || draft, Str("body"), published, assetUrl, assetSize, checksumUrl);
    }
}

/// <summary>Light Markdown → plain-text conversion for showing release notes in a text box.</summary>
public static class ReleaseNotes
{
    private static readonly Regex Image = new(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Html = new(@"<[^>\n]+>", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"(\*\*|__|~~)(.+?)\1", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"(?<![\w*])[*_](?![\s*_])(.+?)(?<![\s*_])[*_](?![\w*])", RegexOptions.Compiled);
    private static readonly Regex Code = new(@"`([^`]*)`", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*([-*_]\s*){3,}$", RegexOptions.Compiled);

    public static string ToPlainText(string? markdown, int maxChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var sb = new StringBuilder();
        var blank = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || Rule.IsMatch(line)) continue;
            line = Heading.Replace(line, "");
            line = Bullet.Replace(line, "$1• ");
            if (line.TrimStart().StartsWith('>')) line = line.TrimStart().TrimStart('>').TrimStart();
            line = Image.Replace(line, "$1");
            line = Link.Replace(line, "$1");
            line = Html.Replace(line, "");
            line = Emphasis.Replace(line, "$2");
            line = Italic.Replace(line, "$1");
            line = Code.Replace(line, "$1");

            if (line.Trim().Length == 0)
            {
                if (sb.Length > 0) blank = true;
                continue;
            }
            if (blank) sb.Append(Environment.NewLine);
            blank = false;
            sb.Append(line).Append(Environment.NewLine);
        }

        var text = sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";
    }
}
