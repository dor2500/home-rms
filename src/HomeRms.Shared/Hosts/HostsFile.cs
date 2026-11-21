using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HomeRms.Shared.Hosts;

/// <summary>Thrown when the hosts file cannot be updated safely (broken markers, invalid content).</summary>
public sealed class HostsFormatException(string message, IReadOnlyList<string>? errors = null) : Exception(message)
{
    public IReadOnlyList<string> Errors { get; } = errors ?? [message];
}

/// <summary>One physical line: its text and the terminator that followed it ("\r\n", "\n", "\r" or "" at EOF).</summary>
public readonly record struct HostsLine(string Text, string Terminator);

/// <summary>
/// Pure hosts-file logic shared by the Windows and Linux agents (no I/O): decoding with the file's
/// own encoding, the Home RMS managed block, domain validation and raw-text validation.
/// <para>
/// The managed block sits between <see cref="BeginMarker"/> and <see cref="EndMarker"/>. Everything outside
/// it is preserved byte-for-byte: the text is decoded with the detected encoding (BOM kept; files without a
/// BOM that are not valid UTF-8 are handled as Latin-1, which round-trips every byte) and only the block
/// lines are replaced. Broken markers (missing, duplicated, out of order or garbled) make every write fail
/// with a clear error instead of guessing.
/// </para>
/// <para>
/// Mapping policy (kept consistent everywhere): each blocked hostname gets one IPv4 line with the mapping
/// IP (<c>0.0.0.0</c>, or the listener's loopback IP while the blocked-access listener runs) and one IPv6
/// <c>::</c> line, so AAAA lookups can never fall through to DNS and bypass the block. The listener only
/// binds IPv4 loopback; IPv6 attempts fail fast and browsers fall back to the IPv4 entry.
/// </para>
/// </summary>
public static class HostsFile
{
    public const string BeginMarker = "# BEGIN HOME-RMS BLOCKLIST";
    public const string EndMarker = "# END HOME-RMS BLOCKLIST";
    public const string MarkerToken = "HOME-RMS BLOCKLIST";
    private const string OptionsPrefix = "# options:";
    public const string BlockedIp = "0.0.0.0";
    public const string BlockedIpv6 = "::";
    public const string DefaultLoopbackIp = "127.0.0.1";
    public const int MaxDomains = 5000;

    public static string WindowsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc", "hosts");

    public const string LinuxPath = "/etc/hosts";

    public static string SystemPath => OperatingSystem.IsWindows() ? WindowsPath : LinuxPath;

    public static string PlatformNewLine => OperatingSystem.IsWindows() ? "\r\n" : "\n";

    // ------------------------------------------------------------------ encoding

    /// <summary>Decoded file: text plus everything needed to write it back with the same bytes.</summary>
    public sealed record Decoded(string Text, Encoding Encoding, byte[] Preamble, string EncodingName)
    {
        public byte[] Encode(string text)
        {
            var body = Encoding.GetBytes(text);
            if (Preamble.Length == 0) return body;
            var all = new byte[Preamble.Length + body.Length];
            Preamble.CopyTo(all, 0);
            body.CopyTo(all, Preamble.Length);
            return all;
        }

        /// <summary>True when <paramref name="text"/> can be stored without losing characters.</summary>
        public bool CanEncode(string text)
        {
            if (Encoding.CodePage != 28591) return true; // only Latin-1 cannot store every character
            foreach (var c in text) if (c > 0xFF) return false;
            return true;
        }
    }

    public static Decoded Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new Decoded(new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3),
                new UTF8Encoding(false), bytes[..3], "UTF-8 with BOM");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new Decoded(new UnicodeEncoding(false, false).GetString(bytes, 2, bytes.Length - 2),
                new UnicodeEncoding(false, false), bytes[..2], "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new Decoded(new UnicodeEncoding(true, false).GetString(bytes, 2, bytes.Length - 2),
                new UnicodeEncoding(true, false), bytes[..2], "UTF-16 BE");
        try
        {
            // Strict UTF-8: valid UTF-8 decodes and re-encodes to exactly the same bytes.
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return new Decoded(text, new UTF8Encoding(false), [], "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // Legacy ANSI file: Latin-1 maps every byte 1:1, so content outside the block survives unchanged.
            return new Decoded(Encoding.Latin1.GetString(bytes), Encoding.Latin1, [], "Latin-1 (legacy)");
        }
    }

    /// <summary>SHA-256 (upper-case hex) of the text as UTF-8 — the optimistic-concurrency token of the raw editor.</summary>
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ------------------------------------------------------------------ lines

    public static List<HostsLine> SplitLines(string text)
    {
        var lines = new List<HostsLine>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            var term = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : c.ToString();
            lines.Add(new HostsLine(text[start..i], term));
            i += term.Length - 1;
            start = i + 1;
        }
        if (start < text.Length) lines.Add(new HostsLine(text[start..], ""));
        return lines;
    }

    /// <summary>The file's dominant line ending, or <paramref name="fallback"/> for files without one.</summary>
    public static string DetectNewLine(string text, string fallback)
    {
        int crlf = 0, lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        if (crlf == 0 && lf == 0) return fallback;
        return crlf >= lf ? "\r\n" : "\n";
    }

    // ------------------------------------------------------------------ managed block

    /// <summary>Location of the managed block (line indexes), or why it cannot be trusted.</summary>
    public sealed record BlockInfo(int BeginLine, int EndLine, string? Error)
    {
        public bool Exists => BeginLine >= 0 && Error is null;
    }

    public static BlockInfo FindBlock(IReadOnlyList<HostsLine> lines)
    {
        var begins = new List<int>();
        var ends = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Text.Trim();
            if (t.IndexOf(MarkerToken, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (string.Equals(t, BeginMarker, StringComparison.Ordinal)) begins.Add(i);
            else if (string.Equals(t, EndMarker, StringComparison.Ordinal)) ends.Add(i);
            else
                return new BlockInfo(-1, -1,
                    $"Line {i + 1}: garbled Home RMS marker '{Clip(t)}' (expected exactly '{BeginMarker}' or '{EndMarker}'). Fix it in the raw editor.");
        }
        if (begins.Count == 0 && ends.Count == 0) return new BlockInfo(-1, -1, null);
        if (begins.Count > 1) return new BlockInfo(-1, -1, $"Duplicate '{BeginMarker}' markers (lines {string.Join(", ", begins.Select(b => b + 1))}).");
        if (ends.Count > 1) return new BlockInfo(-1, -1, $"Duplicate '{EndMarker}' markers (lines {string.Join(", ", ends.Select(e => e + 1))}).");
        if (begins.Count == 0) return new BlockInfo(-1, -1, $"Line {ends[0] + 1}: '{EndMarker}' without a matching '{BeginMarker}'.");
        if (ends.Count == 0) return new BlockInfo(-1, -1, $"Line {begins[0] + 1}: '{BeginMarker}' without a matching '{EndMarker}'.");
        if (ends[0] < begins[0]) return new BlockInfo(-1, -1, $"Line {ends[0] + 1}: '{EndMarker}' appears before '{BeginMarker}' (line {begins[0] + 1}).");
        return new BlockInfo(begins[0], ends[0], null);
    }

    /// <summary>What the managed block currently says.</summary>
    public sealed record ManagedState(IReadOnlyList<string> Hosts, IReadOnlyList<string> Domains, bool IncludeWww, string? MappingIp, BlockInfo Block);

    /// <summary>Reads the managed block. Never throws; check <see cref="ManagedState.Block"/>.Error.</summary>
    public static ManagedState ReadManaged(string text)
    {
        var lines = SplitLines(text);
        var block = FindBlock(lines);
        if (!block.Exists) return new ManagedState([], [], true, null, block);

        var hosts = new SortedSet<string>(StringComparer.Ordinal);
        var includeWww = false;
        string? mapping = null;
        for (var i = block.BeginLine + 1; i < block.EndLine; i++)
        {
            var t = lines[i].Text.Trim();
            if (t.StartsWith(OptionsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                includeWww = t.Contains("include-www=1", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var hash = t.IndexOf('#');
            if (hash >= 0) t = t[..hash].Trim();
            if (t.Length == 0) continue;
            var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (mapping is null && IPAddress.TryParse(parts[0], out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                mapping = parts[0];
            foreach (var h in parts.Skip(1)) hosts.Add(h.ToLowerInvariant().TrimEnd('.'));
        }

        // Base domains: hide the automatic www. variants.
        var domains = hosts.Where(h => !(includeWww && h.StartsWith("www.", StringComparison.Ordinal) && hosts.Contains(h[4..]))).ToList();
        return new ManagedState(hosts.ToList(), domains, includeWww, mapping, block);
    }

    /// <summary>Expands the base domain list into the hostnames the block maps (sorted, deduplicated).</summary>
    public static IReadOnlyList<string> ExpandHosts(IEnumerable<string> domains, bool includeWww)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in domains)
        {
            set.Add(d);
            if (includeWww && !d.StartsWith("www.", StringComparison.Ordinal)) set.Add("www." + d);
        }
        return set.ToList();
    }

    /// <summary>Renders the block lines (markers included), each terminated by <paramref name="newLine"/>.</summary>
    public static string RenderBlock(IReadOnlyList<string> domains, bool includeWww, string mappingIp, string newLine)
    {
        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append(newLine);
        sb.Append("# Managed by Home RMS. Edit the block list from the Home RMS console; lines inside this block are rewritten.").Append(newLine);
        sb.Append(OptionsPrefix).Append(" include-www=").Append(includeWww ? '1' : '0').Append(newLine);
        foreach (var h in ExpandHosts(domains, includeWww))
        {
            sb.Append(mappingIp).Append(' ').Append(h).Append(newLine);
            sb.Append(BlockedIpv6).Append(' ').Append(h).Append(newLine);
        }
        sb.Append(EndMarker).Append(newLine);
        return sb.ToString();
    }

    /// <summary>
    /// Returns <paramref name="text"/> with the managed block replaced (or appended / removed). Lines outside
    /// the block are untouched. An empty domain list removes the block. Throws <see cref="HostsFormatException"/>
    /// when the existing markers are broken.
    /// </summary>
    public static string ApplyBlock(string text, IReadOnlyList<string> domains, bool includeWww, string mappingIp, string fallbackNewLine)
    {
        var lines = SplitLines(text);
        var block = FindBlock(lines);
        if (block.Error is not null) throw new HostsFormatException("The Home RMS block in the hosts file is damaged; nothing was changed. " + block.Error);
        var nl = DetectNewLine(text, fallbackNewLine);
        var rendered = domains.Count == 0 ? "" : RenderBlock(domains, includeWww, mappingIp, nl);

        var sb = new StringBuilder(text.Length + rendered.Length);
        if (block.Exists)
        {
            for (var i = 0; i < block.BeginLine; i++) sb.Append(lines[i].Text).Append(lines[i].Terminator);
            if (rendered.Length > 0)
            {
                // Keep whatever terminator the end marker had (a file may end right after it without a newline).
                var endTerm = lines[block.EndLine].Terminator;
                sb.Append(endTerm.Length > 0 || block.EndLine + 1 < lines.Count ? rendered : rendered[..^nl.Length]);
            }
            for (var i = block.EndLine + 1; i < lines.Count; i++) sb.Append(lines[i].Text).Append(lines[i].Terminator);
            return sb.ToString();
        }

        if (rendered.Length == 0) return text;
        sb.Append(text);
        if (text.Length > 0 && text[^1] != '\n' && text[^1] != '\r') sb.Append(nl);
        sb.Append(rendered);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ domains

    /// <summary>
    /// Normalizes one user-entered domain: trims, strips a URL scheme / path / port, lower-cases, converts
    /// IDN to punycode and validates RFC 1123 labels. Returns null and an error for anything else
    /// (wildcards, IP literals, single labels, &gt; 253 characters).
    /// </summary>
    public static string? NormalizeDomain(string? input, out string? error)
    {
        error = null;
        var s = (input ?? "").Trim();
        if (s.Length == 0) { error = "empty domain"; return null; }
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var cut = s.IndexOfAny(['/', '?', '#']);
        if (cut >= 0) s = s[..cut];
        if (s.Contains('@')) { error = $"'{Clip(input!)}' is not a domain"; return null; }
        if (s.Contains('*')) { error = $"'{Clip(input!)}': wildcards are not supported by the hosts file; list each name"; return null; }
        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon && s[(colon + 1)..].All(char.IsAsciiDigit)) s = s[..colon];
        s = s.TrimEnd('.');
        if (s.Length == 0) { error = $"'{Clip(input!)}' is not a domain"; return null; }
        if (IPAddress.TryParse(s, out _)) { error = $"'{Clip(input!)}' is an IP address, not a domain"; return null; }

        string ascii;
        try { ascii = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true }.GetAscii(s).ToLowerInvariant(); }
        catch (ArgumentException) { error = $"'{Clip(input!)}' is not a valid domain name"; return null; }

        if (!IsValidHostname(ascii, requireDot: true, out var why)) { error = $"'{Clip(input!)}': {why}"; return null; }
        if (ascii == "localhost" || ascii.EndsWith(".localhost", StringComparison.Ordinal)) { error = "localhost cannot be blocked"; return null; }
        return ascii;
    }

    /// <summary>Normalizes, deduplicates and sorts a list; invalid entries are reported, not silently dropped.</summary>
    public static (List<string> Domains, List<string> Errors) NormalizeDomains(IEnumerable<string> inputs)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var d = NormalizeDomain(raw, out var err);
            if (d is null) errors.Add(err!);
            else set.Add(d);
        }
        if (set.Count > MaxDomains) errors.Add($"Too many domains ({set.Count}); the limit is {MaxDomains}.");
        return (set.ToList(), errors);
    }

    /// <summary>RFC 1123 hostname check on an ASCII name (labels 1-63 chars of a-z, 0-9, '-', no leading/trailing '-', total ≤ 253).</summary>
    public static bool IsValidHostname(string name, bool requireDot, out string? why)
    {
        why = null;
        if (name.Length == 0) { why = "empty name"; return false; }
        if (name.Length > 253) { why = "longer than 253 characters"; return false; }
        var labels = name.Split('.');
        if (requireDot && labels.Length < 2) { why = "needs at least two labels (e.g. example.com)"; return false; }
        foreach (var l in labels)
        {
            if (l.Length == 0) { why = "empty label (double dot)"; return false; }
            if (l.Length > 63) { why = $"label '{Clip(l)}' is longer than 63 characters"; return false; }
            if (l[0] == '-' || l[^1] == '-') { why = $"label '{Clip(l)}' starts or ends with '-'"; return false; }
            foreach (var c in l)
            {
                // '_' is tolerated for existing raw entries (service names); user-entered domains go through IdnMapping (STD3) first.
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')) { why = $"invalid character '{c}'"; return false; }
            }
        }
        if (requireDot && labels[^1].All(char.IsAsciiDigit)) { why = "the top-level label is numeric"; return false; }
        return true;
    }

    // ------------------------------------------------------------------ raw validation

    /// <summary>
    /// Validates a full replacement text: every non-comment line must be an IP address followed by one or more
    /// valid hostnames, and the managed block (if any) must be well-formed. Errors carry 1-based line numbers.
    /// </summary>
    public static List<string> ValidateRaw(string text)
    {
        var errors = new List<string>();
        var lines = SplitLines(text);
        for (var i = 0; i < lines.Count && errors.Count < 200; i++)
        {
            var t = lines[i].Text;
            if (t.IndexOf('\0') >= 0) { errors.Add($"Line {i + 1}: contains a NUL character"); continue; }
            var hash = t.IndexOf('#');
            if (hash >= 0) t = t[..hash];
            t = t.Trim();
            if (t.Length == 0) continue;
            var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!IsValidHostsIp(parts[0]))
            {
                errors.Add($"Line {i + 1}: '{Clip(parts[0])}' is not a valid IP address");
                continue;
            }
            if (parts.Length < 2)
            {
                errors.Add($"Line {i + 1}: IP address without a hostname");
                continue;
            }
            foreach (var h in parts.Skip(1))
            {
                if (!IsValidHostname(h.TrimEnd('.'), requireDot: false, out var why))
                    errors.Add($"Line {i + 1}: hostname '{Clip(h)}' is invalid ({why})");
            }
        }
        var block = FindBlock(lines);
        if (block.Error is not null) errors.Add(block.Error);
        return errors;
    }

    /// <summary>Strict IP check: dotted-quad IPv4 (no shorthand like "1" or "127.1") or an IPv6 literal.</summary>
    public static bool IsValidHostsIp(string s)
    {
        if (s.Contains(':')) return IPAddress.TryParse(s, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
            if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || int.Parse(p, CultureInfo.InvariantCulture) > 255) return false;
        return true;
    }

    /// <summary>True for a dotted-quad address inside 127.0.0.0/8.</summary>
    public static bool IsLoopbackV4(string? s) =>
        s is not null && IsValidHostsIp(s) && !s.Contains(':') && s.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>Lines that differ between two block host lists (for "what will change" previews).</summary>
    public static (List<string> Added, List<string> Removed) Diff(IReadOnlyCollection<string> before, IReadOnlyCollection<string> after)
    {
        var b = new HashSet<string>(before, StringComparer.Ordinal);
        var a = new HashSet<string>(after, StringComparer.Ordinal);
        return (after.Where(x => !b.Contains(x)).ToList(), before.Where(x => !a.Contains(x)).ToList());
    }

    private static string Clip(string s) => s.Length <= 60 ? s : s[..60] + "…";
}
