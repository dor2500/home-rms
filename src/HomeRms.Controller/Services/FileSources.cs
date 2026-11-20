using System.IO;
using Grpc.Core;
using HomeRms.Controller.Models;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller.Services;

/// <summary>One row in a file-manager pane.</summary>
public sealed class FileRow
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDir { get; init; }
    public bool IsLink { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public string Attributes { get; init; } = "";

    /// <summary>0 for folders, 1 for files: primary sort key so folders always stay on top.</summary>
    public int DirFirst => IsDir ? 0 : 1;
    public string Glyph => IsDir ? (IsLink ? "[L]" : "[D]") : "";
    public string SizeText => IsDir ? "" : FormatSize(Size);
    public string ModifiedText => Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public string TypeText => IsDir ? "Folder" : Path.GetExtension(Name) is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() + " file" : "File";

    public static string FormatSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B"
    };
}

public sealed record DriveItem(string Name, string Label, long FreeBytes, long TotalBytes)
{
    public string Display => string.IsNullOrWhiteSpace(Label)
        ? Name
        : TotalBytes > 0 ? $"{Name}  {Label}  ({FileRow.FormatSize(FreeBytes)} free)" : $"{Name}  {Label}";
}

public sealed record DirListingResult(string Path, string Parent, IReadOnlyList<FileRow> Entries, bool Truncated);

/// <summary>Abstraction over the local disk and a remote agent's file system.</summary>
public interface IFileSource
{
    bool IsRemote { get; }
    string DisplayName { get; }
    Task<IReadOnlyList<DriveItem>> GetDrivesAsync(CancellationToken ct);
    Task<DirListingResult> ListAsync(string path, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task RenameAsync(string source, string destination, CancellationToken ct);
    Task DeleteAsync(string path, bool recursive, CancellationToken ct);
    string Combine(string dir, string name);
}

public sealed class LocalFileSource : IFileSource
{
    public bool IsRemote => false;
    public string DisplayName => $"{Environment.MachineName} (this PC)";

    public Task<IReadOnlyList<DriveItem>> GetDrivesAsync(CancellationToken ct) => Task.Run<IReadOnlyList<DriveItem>>(() =>
        DriveInfo.GetDrives().Select(d =>
        {
            try { return d.IsReady ? new DriveItem(d.Name, d.VolumeLabel, d.AvailableFreeSpace, d.TotalSize) : new DriveItem(d.Name, d.DriveType.ToString(), 0, 0); }
            catch { return new DriveItem(d.Name, "", 0, 0); }
        }).ToList(), ct);

    public Task<DirListingResult> ListAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        if (!dir.Exists) throw new DirectoryNotFoundException($"Folder not found: {dir.FullName}");
        var rows = new List<FileRow>();
        foreach (var fsi in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            var isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
            rows.Add(new FileRow
            {
                Name = fsi.Name,
                FullPath = fsi.FullName,
                IsDir = isDir,
                IsLink = (fsi.Attributes & FileAttributes.ReparsePoint) != 0,
                Size = fsi is FileInfo fi ? fi.Length : 0,
                Modified = fsi.LastWriteTimeUtc,
                Attributes = fsi.Attributes.ToString()
            });
            if (rows.Count >= 10000) break;
        }
        return new DirListingResult(dir.FullName, dir.Parent?.FullName ?? "", rows, rows.Count >= 10000);
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException($"'{Path.GetFileName(path)}' already exists.");
        Directory.CreateDirectory(path);
    }, ct);

    public Task RenameAsync(string source, string destination, CancellationToken ct) => Task.Run(() =>
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }, ct);

    public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => Task.Run(() =>
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive);
        else File.Delete(path);
    }, ct);

    public string Combine(string dir, string name) => Path.Combine(dir, name);
}

/// <summary>Remote file system of one agent (all calls go through the pairing-aware pool).</summary>
public sealed class RemoteFileSource(AgentConnectionPool pool, MachineProfile endpoint) : IFileSource
{
    public MachineProfile Endpoint => endpoint;
    public bool IsRemote => true;
    public string DisplayName => endpoint.DisplayTitle;

    private static DateTime Deadline(int seconds = 30) => DateTime.UtcNow.AddSeconds(seconds);

    public async Task<IReadOnlyList<DriveItem>> GetDrivesAsync(CancellationToken ct)
    {
        var list = await pool.CallAsync(endpoint, true, c => c.ListDrivesAsync(new Empty(), deadline: Deadline(), cancellationToken: ct).ResponseAsync, ct);
        return list.Drives.Select(d => new DriveItem(d.Name, d.IsReady ? d.Label : d.DriveType, d.FreeBytes, d.TotalBytes)).ToList();
    }

    public async Task<DirListingResult> ListAsync(string path, CancellationToken ct)
    {
        var res = await pool.CallAsync(endpoint, true, c => c.ListDirectoryAsync(
            new ListDirectoryRequest { Path = path, MaxEntries = 10000 }, deadline: Deadline(60), cancellationToken: ct).ResponseAsync, ct);
        var rows = res.Entries.Select(e => new FileRow
        {
            Name = e.Name,
            FullPath = e.FullPath,
            IsDir = e.IsDir,
            IsLink = e.IsLink,
            Size = e.Size,
            Modified = e.ModifiedUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(e.ModifiedUnixMs).UtcDateTime : null,
            Attributes = e.Attributes
        }).ToList();
        return new DirListingResult(res.Path, res.ParentPath, rows, res.Truncated);
    }

    private static void Check(FileOpResult r)
    {
        if (!r.Success) throw new IOException(string.IsNullOrWhiteSpace(r.Message) ? "The agent reported a failure." : r.Message);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        Check(await pool.CallAsync(endpoint, true, c => c.CreateDirectoryAsync(new FilePathRequest { Path = path }, deadline: Deadline(), cancellationToken: ct).ResponseAsync, ct));

    public async Task RenameAsync(string source, string destination, CancellationToken ct) =>
        Check(await pool.CallAsync(endpoint, true, c => c.RenamePathAsync(
            new RenamePathRequest { SourcePath = source, DestinationPath = destination }, deadline: Deadline(), cancellationToken: ct).ResponseAsync, ct));

    public async Task DeleteAsync(string path, bool recursive, CancellationToken ct) =>
        Check(await pool.CallAsync(endpoint, true, c => c.DeletePathAsync(
            new DeletePathRequest { Path = path, Recursive = recursive }, deadline: Deadline(300), cancellationToken: ct).ResponseAsync, ct));

    public string Combine(string dir, string name)
    {
        var sep = dir.Contains('/') && !dir.Contains('\\') ? '/' : '\\';
        return dir.EndsWith(sep) ? dir + name : dir + sep + name;
    }

    public static bool IsAlreadyExists(Exception ex) => ex is RpcException { StatusCode: StatusCode.AlreadyExists };
}
