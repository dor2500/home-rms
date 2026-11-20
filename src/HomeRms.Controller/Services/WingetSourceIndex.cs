using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace HomeRms.Controller.Services;

/// <summary>One package row from the official winget source index.</summary>
public sealed record IndexPackage(string Id, string Name, string Moniker, string Version, IReadOnlyList<string> Tags, string NormPublisher);

/// <summary>Snapshot of the local index state, for the Deploy panel status line.</summary>
public sealed record IndexStatus(bool Available, int PackageCount, DateTime? UpdatedUtc, DateTime? CheckedUtc, string? Error);

/// <summary>
/// Microsoft's official winget community source ("winget", https://cdn.winget.microsoft.com/cache).
/// source2.msix is a signed MSIX (a zip) containing Public/index.db, a SQLite database with the
/// table <c>packages(id, name, moniker, latest_version, ...)</c> plus <c>tags2</c>/<c>tags2_map</c> and
/// <c>norm_publishers2</c>. The database is cached under %LOCALAPPDATA%\HomeRMS\winget-index and
/// re-checked at most once per day with a conditional request (ETag / Last-Modified).
/// </summary>
public sealed class WingetSourceIndex
{
    public const string SourceUrl = "https://cdn.winget.microsoft.com/cache/source2.msix";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private static readonly string IndexDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeRMS", "winget-index");
    private static readonly string DbPath = Path.Combine(IndexDir, "index.db");
    private static readonly string MetaPath = Path.Combine(IndexDir, "index-meta.json");

    private static readonly HttpClient Http = CreateHttp();

    private readonly object _dbLock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IndexMeta _meta = new();
    private int _packageCount;
    private DateTime? _dbWriteTimeUtc;
    private string? _lastError;

    public WingetSourceIndex()
    {
        try
        {
            if (File.Exists(MetaPath))
                _meta = JsonSerializer.Deserialize<IndexMeta>(File.ReadAllText(MetaPath)) ?? new IndexMeta();
        }
        catch (Exception ex)
        {
            _lastError = $"Index metadata unreadable: {ex.Message}";
        }
        TryOpenExisting();
    }

    public bool IsAvailable => _packageCount > 0;

    public IndexStatus Status => new(IsAvailable, _packageCount,
        _dbWriteTimeUtc ?? _meta.LastModifiedUtc, _meta.CheckedUtc, _lastError);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(90),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HomeRMS-Controller/2.1");
        return http;
    }

    private static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DbPath,
        Mode = SqliteOpenMode.ReadOnly,
        // No pooling: the file must not stay locked so a refreshed index can replace it.
        Pooling = false,
    }.ToString();

    private void TryOpenExisting()
    {
        if (!File.Exists(DbPath)) return;
        try
        {
            lock (_dbLock)
            {
                using var conn = new SqliteConnection(ConnectionString);
                conn.Open();
                (_packageCount, _dbWriteTimeUtc) = ReadDbInfo(conn);
            }
        }
        catch (Exception ex)
        {
            _packageCount = 0;
            _lastError = $"Cached index is unreadable ({ex.Message}); it will be downloaded again.";
        }
    }

    private static (int Count, DateTime? WriteTimeUtc) ReadDbInfo(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM packages";
        var count = Convert.ToInt32(cmd.ExecuteScalar());

        DateTime? written = null;
        cmd.CommandText = "SELECT value FROM metadata WHERE name = 'lastwritetime'";
        if (cmd.ExecuteScalar() is string s && long.TryParse(s, out var unix) && unix > 0)
            written = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return (count, written);
    }

    /// <summary>
    /// Makes sure a usable index is on disk. Hits the network only when the cached copy is older
    /// than a day (or <paramref name="force"/> is set), and then only with a conditional GET.
    /// Throws on failure; a previously cached index stays usable in that case.
    /// </summary>
    public async Task EnsureFreshAsync(bool force, CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var due = force || !IsAvailable || _meta.CheckedUtc is null ||
                      DateTime.UtcNow - _meta.CheckedUtc.Value > RefreshInterval;
            if (!due) return;

            try
            {
                await DownloadAsync(ct);
                _lastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _lastError = ex is TaskCanceledException ? "Index download timed out." : ex.Message;
                throw;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(IndexDir);
        using var req = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
        if (IsAvailable)
        {
            if (!string.IsNullOrEmpty(_meta.ETag) && System.Net.Http.Headers.EntityTagHeaderValue.TryParse(_meta.ETag, out var etag))
                req.Headers.IfNoneMatch.Add(etag);
            if (_meta.LastModifiedUtc is { } lm)
                req.Headers.IfModifiedSince = new DateTimeOffset(lm, TimeSpan.Zero);
        }

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotModified && IsAvailable)
        {
            _meta.CheckedUtc = DateTime.UtcNow;
            SaveMeta();
            return;
        }
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Index download failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var tmpMsix = Path.Combine(IndexDir, "source2.msix.tmp");
        var tmpDb = Path.Combine(IndexDir, "index.db.tmp");
        try
        {
            await using (var fs = File.Create(tmpMsix))
                await resp.Content.CopyToAsync(fs, ct);

            using (var zip = ZipFile.OpenRead(tmpMsix))
            {
                var entry = zip.GetEntry("Public/index.db")
                    ?? throw new InvalidDataException("source2.msix does not contain Public/index.db (format changed?).");
                entry.ExtractToFile(tmpDb, overwrite: true);
            }

            // Validate before swapping it in.
            int count;
            DateTime? written;
            using (var probe = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = tmpDb, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                probe.Open();
                (count, written) = ReadDbInfo(probe);
            }
            if (count == 0) throw new InvalidDataException("Downloaded index contains no packages.");

            lock (_dbLock)
            {
                File.Move(tmpDb, DbPath, overwrite: true);
                _packageCount = count;
                _dbWriteTimeUtc = written;
            }

            _meta.ETag = resp.Headers.ETag?.ToString();
            _meta.LastModifiedUtc = resp.Content.Headers.LastModified?.UtcDateTime;
            _meta.CheckedUtc = DateTime.UtcNow;
            SaveMeta();
        }
        finally
        {
            TryDelete(tmpMsix);
            TryDelete(tmpDb);
        }
    }

    private void SaveMeta()
    {
        HomeRms.Shared.AtomicFile.WriteAllText(MetaPath, JsonSerializer.Serialize(_meta));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Ranked search: exact id / moniker &gt; name prefix &gt; id prefix &gt; name contains &gt; id contains &gt; tag / publisher.
    /// <paramref name="categoryTags"/> (lower-case winget tags) restricts results to packages carrying any of them.
    /// </summary>
    public (List<IndexPackage> Items, int Total) Search(string query, IReadOnlyCollection<string>? categoryTags, int offset, int limit)
    {
        if (!IsAvailable) return ([], 0);
        query = query.Trim();

        var where = new StringBuilder("1=1");
        var hasQuery = query.Length > 0;
        if (hasQuery)
        {
            where.Append("""
                 AND (p.id LIKE $contains ESCAPE '\' OR p.name LIKE $contains ESCAPE '\' OR p.moniker LIKE $contains ESCAPE '\'
                      OR p.rowid IN (SELECT m.package FROM tags2_map m JOIN tags2 t ON t.rowid = m.tag WHERE t.tag = $lower)
                      OR p.rowid IN (SELECT np.package FROM norm_publishers2 np WHERE np.norm_publisher LIKE $contains ESCAPE '\'))
                """);
        }

        // Uncorrelated IN-subqueries: SQLite evaluates them once (the map tables are keyed by tag, not package).
        var tagParams = new List<string>();
        if (categoryTags is { Count: > 0 })
        {
            for (var i = 0; i < categoryTags.Count; i++) tagParams.Add($"$tag{i}");
            where.Append($" AND p.rowid IN (SELECT m2.package FROM tags2_map m2 JOIN tags2 t2 ON t2.rowid = m2.tag WHERE t2.tag IN ({string.Join(",", tagParams)}))");
        }

        const string rank = """
            CASE WHEN p.id = $q COLLATE NOCASE THEN 0
                 WHEN p.moniker = $q COLLATE NOCASE THEN 1
                 WHEN p.name = $q COLLATE NOCASE THEN 2
                 WHEN p.name LIKE $prefix ESCAPE '\' THEN 3
                 WHEN p.id LIKE $prefix ESCAPE '\' THEN 4
                 WHEN p.name LIKE $contains ESCAPE '\' THEN 5
                 WHEN p.id LIKE $contains ESCAPE '\' THEN 6
                 ELSE 7 END
            """;

        lock (_dbLock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            void Bind(SqliteCommand c)
            {
                c.Parameters.AddWithValue("$q", query);
                c.Parameters.AddWithValue("$lower", query.ToLowerInvariant());
                c.Parameters.AddWithValue("$prefix", EscapeLike(query) + "%");
                c.Parameters.AddWithValue("$contains", "%" + EscapeLike(query) + "%");
                var i = 0;
                foreach (var t in categoryTags ?? [])
                    c.Parameters.AddWithValue($"$tag{i++}", t);
            }

            int total;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM packages p WHERE {where}";
                Bind(countCmd);
                total = Convert.ToInt32(countCmd.ExecuteScalar());
            }

            var rows = new List<(long RowId, string Id, string Name, string Moniker, string Version)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT p.rowid, p.id, p.name, IFNULL(p.moniker, ''), p.latest_version
                    FROM packages p WHERE {where}
                    ORDER BY {(hasQuery ? rank + "," : "")} length(p.name), p.name COLLATE NOCASE
                    LIMIT $limit OFFSET $offset
                    """;
                Bind(cmd);
                cmd.Parameters.AddWithValue("$limit", limit);
                cmd.Parameters.AddWithValue("$offset", offset);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            }

            return (Hydrate(conn, rows), total);
        }
    }

    /// <summary>Case-insensitive exact-id lookup (used to resolve imported pack IDs).</summary>
    public Dictionary<string, IndexPackage> Lookup(IEnumerable<string> ids)
    {
        var result = new Dictionary<string, IndexPackage>(StringComparer.OrdinalIgnoreCase);
        if (!IsAvailable) return result;
        var list = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return result;

        lock (_dbLock)
        {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();
            var rows = new List<(long, string, string, string, string)>();
            foreach (var chunk in list.Chunk(200))
            {
                using var cmd = conn.CreateCommand();
                var names = chunk.Select((_, i) => $"$id{i}").ToList();
                cmd.CommandText = $"SELECT rowid, id, name, IFNULL(moniker, ''), latest_version FROM packages WHERE id COLLATE NOCASE IN ({string.Join(",", names)})";
                for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue(names[i], chunk[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            }
            foreach (var p in Hydrate(conn, rows))
                result[p.Id] = p;
        }
        return result;
    }

    private static List<IndexPackage> Hydrate(SqliteConnection conn, List<(long RowId, string Id, string Name, string Moniker, string Version)> rows)
    {
        if (rows.Count == 0) return [];
        var tags = new Dictionary<long, List<string>>();
        var pubs = new Dictionary<long, string>();
        var rowIds = string.Join(",", rows.Select(r => r.RowId)); // integers only: safe to inline

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT m.package, t.tag FROM tags2_map m JOIN tags2 t ON t.rowid = m.tag WHERE m.package IN ({rowIds})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var pkg = r.GetInt64(0);
                if (!tags.TryGetValue(pkg, out var l)) tags[pkg] = l = [];
                l.Add(r.GetString(1));
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT package, norm_publisher FROM norm_publishers2 WHERE package IN ({rowIds})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) pubs.TryAdd(r.GetInt64(0), r.GetString(1));
        }

        return rows.Select(x => new IndexPackage(x.Id, x.Name, x.Moniker, x.Version,
            tags.TryGetValue(x.RowId, out var t) ? t : [],
            pubs.TryGetValue(x.RowId, out var p) ? p : "")).ToList();
    }

    private sealed class IndexMeta
    {
        public string? ETag { get; set; }
        public DateTime? LastModifiedUtc { get; set; }
        public DateTime? CheckedUtc { get; set; }
    }
}
