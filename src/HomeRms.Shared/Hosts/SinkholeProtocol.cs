using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace HomeRms.Shared.Hosts;

public enum SniParseStatus
{
    /// <summary>A server_name (host_name) was found.</summary>
    Found,
    /// <summary>The bytes so far are a valid prefix; read more.</summary>
    NeedMore,
    /// <summary>A complete ClientHello without a host_name entry.</summary>
    NoSni,
    /// <summary>Not a TLS handshake / malformed.</summary>
    Invalid,
}

/// <summary>
/// Pure parsers used by the sinkhole listener: TLS ClientHello SNI extraction (no decryption, no TLS
/// library involved) and the HTTP Host header. Every read is bounds-checked; garbage never throws.
/// </summary>
public static class SinkholeProtocol
{
    public const int MaxPeekBytes = 16 * 1024;

    /// <summary>
    /// Extracts the SNI host name from the first bytes a client sent. Handshake messages fragmented over
    /// several TLS records are reassembled (bounded by <see cref="MaxPeekBytes"/>).
    /// </summary>
    public static SniParseStatus TryParseSni(ReadOnlySpan<byte> data, out string? host)
    {
        host = null;
        try
        {
            return Parse(data, out host);
        }
        catch (Exception)
        {
            // Defensive: every access below is bounds-checked, but a parser on untrusted input must never throw.
            host = null;
            return SniParseStatus.Invalid;
        }
    }

    private static SniParseStatus Parse(ReadOnlySpan<byte> data, out string? host)
    {
        host = null;
        if (data.Length == 0) return SniParseStatus.NeedMore;
        if (data[0] != 0x16) return SniParseStatus.Invalid; // handshake record
        if (data.Length >= 2 && data[1] != 0x03) return SniParseStatus.Invalid; // SSL 3.0 / TLS 1.x major version

        // Reassemble the handshake payload from consecutive handshake records.
        var full = data.Length >= MaxPeekBytes; // the caller will not read more
        if (full) data = data[..MaxPeekBytes];
        var hs = new byte[data.Length];
        var hsLen = 0;
        var pos = 0;
        var needLen = -1;
        var truncated = false;
        while (true)
        {
            if (data.Length - pos < 5)
                return full && hsLen > 4 ? ParseClientHello(hs.AsSpan(0, hsLen), truncated: true, out host) : SniParseStatus.NeedMore;
            if (data[pos] != 0x16 || data[pos + 1] != 0x03) return SniParseStatus.Invalid;
            var recLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 3, 2));
            if (recLen == 0 || recLen > 16384 + 2048) return SniParseStatus.Invalid;
            var available = Math.Min(recLen, data.Length - pos - 5);
            data.Slice(pos + 5, available).CopyTo(hs.AsSpan(hsLen));
            hsLen += available;

            if (needLen < 0 && hsLen >= 4)
            {
                if (hs[0] != 0x01) return SniParseStatus.Invalid; // ClientHello
                needLen = 4 + (hs[1] << 16 | hs[2] << 8 | hs[3]);
                if (needLen > MaxPeekBytes) { needLen = MaxPeekBytes; truncated = true; } // SNI normally comes early
            }
            if (needLen > 0 && hsLen >= needLen)
                return ParseClientHello(hs.AsSpan(0, needLen), truncated, out host);
            if (available < recLen)
                return full && hsLen > 4 ? ParseClientHello(hs.AsSpan(0, hsLen), truncated: true, out host) : SniParseStatus.NeedMore;
            pos += 5 + recLen;
        }
    }

    private static SniParseStatus ParseClientHello(ReadOnlySpan<byte> msg, bool truncated, out string? host)
    {
        host = null;
        var incomplete = truncated ? SniParseStatus.NoSni : SniParseStatus.Invalid;
        if (msg.Length < 4 || msg[0] != 0x01) return SniParseStatus.Invalid;
        var p = 4;
        p += 2 + 32; // client_version + random
        if (p + 1 > msg.Length) return incomplete;
        p += 1 + msg[p]; // session_id
        if (p + 2 > msg.Length) return incomplete;
        p += 2 + BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(p, 2)); // cipher_suites
        if (p + 1 > msg.Length) return incomplete;
        p += 1 + msg[p]; // compression_methods
        if (p == msg.Length) return SniParseStatus.NoSni; // no extensions at all
        if (p + 2 > msg.Length) return incomplete;
        var extEnd = Math.Min(msg.Length, p + 2 + BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(p, 2)));
        p += 2;
        while (p + 4 <= extEnd)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(p, 2));
            var len = BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(p + 2, 2));
            p += 4;
            if (p + len > extEnd) return incomplete;
            if (type == 0x0000)
                return ParseServerName(msg.Slice(p, len), out host);
            p += len;
        }
        return p == extEnd || truncated ? SniParseStatus.NoSni : SniParseStatus.Invalid;
    }

    private static SniParseStatus ParseServerName(ReadOnlySpan<byte> ext, out string? host)
    {
        host = null;
        if (ext.Length < 2) return SniParseStatus.Invalid;
        var listEnd = Math.Min(ext.Length, 2 + BinaryPrimitives.ReadUInt16BigEndian(ext[..2]));
        var p = 2;
        while (p + 3 <= listEnd)
        {
            var nameType = ext[p];
            var len = BinaryPrimitives.ReadUInt16BigEndian(ext.Slice(p + 1, 2));
            p += 3;
            if (p + len > listEnd) return SniParseStatus.Invalid;
            if (nameType == 0)
            {
                var name = ext.Slice(p, len);
                if (len == 0 || len > 253) return SniParseStatus.Invalid;
                foreach (var b in name)
                    if (b < 0x21 || b > 0x7E) return SniParseStatus.Invalid; // host_name is ASCII (A-labels)
                host = NormalizeHost(Encoding.ASCII.GetString(name));
                return host is null ? SniParseStatus.Invalid : SniParseStatus.Found;
            }
            p += len;
        }
        return SniParseStatus.NoSni;
    }

    /// <summary>
    /// Parses the Host header out of an HTTP/1.x request head. Returns null while the head is incomplete
    /// (no blank line yet) unless <paramref name="final"/> is set. The port is stripped.
    /// </summary>
    public static string? TryParseHttpHost(ReadOnlySpan<byte> data, bool final, out bool complete)
    {
        complete = false;
        try
        {
            var len = Math.Min(data.Length, MaxPeekBytes);
            var text = Encoding.Latin1.GetString(data[..len]);
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd < 0) headEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
            complete = headEnd >= 0;
            if (!complete && !final) return null;
            var head = complete ? text[..headEnd] : text;
            var lines = head.Split('\n');
            if (lines.Length == 0 || !LooksLikeRequestLine(lines[0])) return null;
            foreach (var raw in lines.Skip(1))
            {
                var line = raw.TrimEnd('\r');
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (!line[..colon].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(colon + 1)..].Trim();
                if (value.StartsWith('['))
                    return null; // IPv6 literal, never a blocked name
                var c = value.IndexOf(':');
                if (c >= 0) value = value[..c];
                return NormalizeHost(value);
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool LooksLikeRequestLine(string line)
    {
        var sp = line.IndexOf(' ');
        if (sp <= 0 || sp > 10) return false;
        foreach (var c in line.AsSpan(0, sp)) if (c is < 'A' or > 'Z') return false;
        return line.Contains(" HTTP/", StringComparison.Ordinal);
    }

    /// <summary>Lower-case, no trailing dot, plausible hostname characters only; otherwise null.</summary>
    public static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var h = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length is 0 or > 253) return null;
        foreach (var c in h)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')) return null;
        return h;
    }

    /// <summary>The managed entry a requested host belongs to: exact match, or the bare name of a www. host.</summary>
    public static string? MatchBlocked(string host, IReadOnlySet<string> blocked)
    {
        if (blocked.Contains(host)) return host;
        if (host.StartsWith("www.", StringComparison.Ordinal) && blocked.Contains(host[4..])) return host;
        return null;
    }
}

/// <summary>
/// Rate limits blocked-site events: at most one event per (endpoint, domain) every <see cref="PerDomainWindow"/>
/// and at most <see cref="GlobalPerMinute"/> events in any rolling minute. Thread-safe; the clock is injectable for tests.
/// </summary>
public sealed class BlockedEventThrottle(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan DefaultPerDomainWindow = TimeSpan.FromSeconds(45);
    public const int DefaultGlobalPerMinute = 30;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly object _gate = new();

    public TimeSpan PerDomainWindow { get; init; } = DefaultPerDomainWindow;
    public int GlobalPerMinute { get; init; } = DefaultGlobalPerMinute;

    /// <summary>True when an event for this endpoint/domain may be emitted now (and records it).</summary>
    public bool ShouldEmit(string endpoint, string domain)
    {
        var now = _clock();
        var key = endpoint + "|" + domain;
        lock (_gate)
        {
            if (_last.TryGetValue(key, out var last) && now - last < PerDomainWindow) return false;
            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(1)) _recent.Dequeue();
            if (_recent.Count >= GlobalPerMinute) return false;
            _recent.Enqueue(now);
            _last[key] = now;
            if (_last.Count > 4096)
                foreach (var (k, t) in _last)
                    if (now - t >= PerDomainWindow) _last.TryRemove(k, out _);
            return true;
        }
    }
}
