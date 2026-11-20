using Google.Protobuf;
using Grpc.Core;
using HomeRms.Shared.Protocol;
using Microsoft.AspNetCore.Http.Features;

namespace HomeRms.Shared.Server;

/// <summary>
/// Path validation for the file manager. Only plain absolute local paths are accepted:
/// Windows rejects UNC (\\server\share), device/namespace paths (\\?\, \\.\), alternate data
/// streams (any ':' after the drive letter), drive-relative paths (C:foo) and reserved device
/// names (CON, NUL, COM1 ...). Unix requires a leading '/'. Everything is normalized with
/// <see cref="Path.GetFullPath(string)"/>, so ".." segments cannot escape validation.
/// </summary>
public static class SafePath
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));

    /// <summary>Validates and normalizes <paramref name="raw"/>; throws INVALID_ARGUMENT when unsafe.</summary>
    public static string Normalize(string? raw, string field = "path")
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw Invalid($"{field} is required.");
        if (raw.Length > 32_000)
            throw Invalid($"{field} is too long.");
        if (raw.Any(char.IsControl))
            throw Invalid($"{field} contains control characters.");

        return OperatingSystem.IsWindows() ? NormalizeWindows(raw, field) : NormalizeUnix(raw, field);
    }

    private static string NormalizeWindows(string raw, string field)
    {
        var p = raw.Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            p.StartsWith(@"\??\", StringComparison.Ordinal))
            throw Invalid($"{field}: device / namespace paths (\\\\?\\, \\\\.\\) are not allowed.");
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
            throw Invalid($"{field}: UNC network paths are not allowed.");
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\')
            throw Invalid($"{field} must be an absolute local path such as C:\\folder.");
        if (p.IndexOf(':', 2) >= 0)
            throw Invalid($"{field}: alternate data streams (':' after the drive letter) are not allowed.");
        if (p.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            throw Invalid($"{field} contains characters that are not valid in a Windows path.");

        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception ex) { throw Invalid($"{field} is not a valid path: {ex.Message}"); }

        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
            throw Invalid($"{field} resolves to a disallowed location.");

        foreach (var segment in full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (ReservedWindowsNames.Contains(stem))
                throw Invalid($"{field}: reserved device name '{segment}' is not allowed.");
        }

        return TrimTrailingSeparator(full);
    }

    private static string NormalizeUnix(string raw, string field)
    {
        if (!raw.StartsWith('/'))
            throw Invalid($"{field} must be an absolute path starting with '/'.");
        string full;
        try { full = Path.GetFullPath(raw); }
        catch (Exception ex) { throw Invalid($"{field} is not a valid path: {ex.Message}"); }
        return TrimTrailingSeparator(full);
    }

    private static string TrimTrailingSeparator(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? Path.TrimEndingDirectorySeparator(full) : full;
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Locations that must never be deleted or renamed: drive roots / "/", the Windows directory and
    /// a few top-level system folders on Windows, and every top-level directory on Unix (/etc, /usr ...).
    /// </summary>
    public static bool IsProtected(string normalizedFullPath)
    {
        var full = normalizedFullPath;
        var root = Path.GetPathRoot(full);
        if (root is not null && SamePath(full, root)) return true;

        if (!OperatingSystem.IsWindows())
        {
            // "/x" (one segment below the root) = system top-level directory
            return full.Count(c => c == '/') <= 1;
        }

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(winDir)) winDir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string?[] protectedDirs =
        [
            winDir,
            Environment.GetEnvironmentVariable("SystemRoot"),
            Path.Combine(winDir, "System32"),
            Path.Combine(winDir, "SysWOW64"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            string.IsNullOrEmpty(userProfile) ? null : Path.GetDirectoryName(userProfile), // C:\Users
        ];
        return protectedDirs.Any(d => !string.IsNullOrEmpty(d) && SamePath(full, d));
    }
}

/// <summary>File manager RPC implementations shared by the Windows and Linux agents.</summary>
public sealed class FileManagerOps(Action<string> log)
{
    public const int ChunkSize = 256 * 1024;
    public const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxListEntries = 10_000;

    private static RpcException Error(StatusCode code, string message) => new(new Status(code, message));

    private RpcException Fail(string op, string what, Exception ex)
    {
        var rpc = ex switch
        {
            RpcException r => r,
            UnauthorizedAccessException => Error(StatusCode.PermissionDenied, "Access denied: " + ex.Message),
            FileNotFoundException or DirectoryNotFoundException => Error(StatusCode.NotFound, ex.Message),
            PathTooLongException => Error(StatusCode.InvalidArgument, ex.Message),
            OperationCanceledException => Error(StatusCode.Cancelled, "Operation cancelled."),
            IOException => Error(StatusCode.FailedPrecondition, ex.Message),
            _ => Error(StatusCode.Internal, ex.Message),
        };
        log($"[files] '{op}' {what} FAILED: {rpc.Status.Detail}");
        return rpc;
    }

    public DriveList ListDrives(string op)
    {
        var result = new DriveList();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!OperatingSystem.IsWindows() &&
                d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network or DriveType.CDRom))
                continue;

            var entry = new DriveEntry { Name = d.Name, DriveType = d.DriveType.ToString() };
            try
            {
                entry.IsReady = d.IsReady;
                if (d.IsReady)
                {
                    entry.Label = d.VolumeLabel ?? "";
                    entry.Format = d.DriveFormat ?? "";
                    entry.TotalBytes = d.TotalSize;
                    entry.FreeBytes = d.AvailableFreeSpace;
                }
            }
            catch { entry.IsReady = false; }

            if (!OperatingSystem.IsWindows() && entry.IsReady && entry.TotalBytes == 0) continue;
            result.Drives.Add(entry);
        }
        log($"[files] '{op}' listed drives ({result.Drives.Count})");
        return result;
    }

    public DirectoryListing ListDirectory(ListDirectoryRequest request, string op)
    {
        try
        {
            var full = SafePath.Normalize(request.Path);
            var di = new DirectoryInfo(full);
            if (!di.Exists)
                throw File.Exists(full)
                    ? Error(StatusCode.InvalidArgument, $"'{full}' is a file, not a directory.")
                    : Error(StatusCode.NotFound, $"Directory '{full}' does not exist.");

            var max = request.MaxEntries <= 0 ? MaxListEntries : Math.Clamp(request.MaxEntries, 1, MaxListEntries);
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };

            var items = new List<FileSystemInfo>();
            var truncated = false;
            foreach (var fsi in di.EnumerateFileSystemInfos("*", options))
            {
                if (items.Count >= max) { truncated = true; break; }
                items.Add(fsi);
            }

            var listing = new DirectoryListing
            {
                Path = full,
                ParentPath = di.Parent?.FullName ?? "",
                Truncated = truncated,
            };
            foreach (var fsi in items
                         .OrderByDescending(i => i is DirectoryInfo)
                         .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                listing.Entries.Add(ToEntry(fsi));

            log($"[files] '{op}' listed '{full}' ({listing.Entries.Count} entries{(truncated ? ", truncated" : "")})");
            return listing;
        }
        catch (Exception ex) { throw Fail(op, $"list '{request.Path}'", ex); }
    }

    private static FileEntry ToEntry(FileSystemInfo fsi)
    {
        var e = new FileEntry { Name = fsi.Name, FullPath = fsi.FullName, IsDir = fsi is DirectoryInfo };
        try
        {
            var attrs = fsi.Attributes;
            e.Attributes = attrs.ToString();
            e.IsLink = (attrs & FileAttributes.ReparsePoint) != 0 || fsi.LinkTarget is not null;
        }
        catch { }
        try { if (fsi is FileInfo fi) e.Size = fi.Length; } catch { }
        try { e.ModifiedUnixMs = new DateTimeOffset(fsi.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(); } catch { }
        return e;
    }

    public async Task DownloadFile(FilePathRequest request, IServerStreamWriter<FileChunk> writer, ServerCallContext context, string op)
    {
        string full = request.Path;
        try
        {
            full = SafePath.Normalize(request.Path);
            if (Directory.Exists(full))
                throw Error(StatusCode.InvalidArgument, $"'{full}' is a directory.");
            if (!File.Exists(full))
                throw Error(StatusCode.NotFound, $"File '{full}' does not exist.");

            await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var total = fs.Length;
            var name = Path.GetFileName(full);
            log($"[files] '{op}' downloading '{full}' ({total:N0} bytes)");

            var buffer = new byte[ChunkSize];
            long offset = 0;
            var first = true;
            while (true)
            {
                var n = await fs.ReadAsync(buffer, context.CancellationToken);
                if (n == 0 && !first) break;
                await writer.WriteAsync(new FileChunk
                {
                    Data = ByteString.CopyFrom(buffer, 0, n),
                    Offset = offset,
                    TotalSize = total,
                    FileName = first ? name : "",
                }, context.CancellationToken);
                offset += n;
                first = false;
                if (n == 0) break;
            }
            log($"[files] '{op}' downloaded '{full}' ({offset:N0} bytes)");
        }
        catch (Exception ex) { throw Fail(op, $"download '{full}'", ex); }
    }

    public async Task<FileOpResult> UploadFile(IAsyncStreamReader<FileUploadChunk> reader, ServerCallContext context, string op)
    {
        // Allow large client-streamed bodies (Kestrel's global limit is lower); our own cap applies below.
        var sizeFeature = context.GetHttpContext().Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = MaxUploadBytes + 64L * 1024 * 1024;

        string? dest = null;
        string? temp = null;
        FileStream? fs = null;
        var overwrite = false;
        long written = 0;
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(context.CancellationToken))
            {
                if (dest is null)
                {
                    if (string.IsNullOrWhiteSpace(chunk.DestinationPath))
                        throw Error(StatusCode.InvalidArgument, "The first upload chunk must carry destination_path.");
                    dest = SafePath.Normalize(chunk.DestinationPath, "destination_path");
                    overwrite = chunk.Overwrite;

                    if (chunk.TotalSize > MaxUploadBytes)
                        throw Error(StatusCode.ResourceExhausted, $"Upload exceeds the {MaxUploadBytes / (1024 * 1024 * 1024)} GB limit.");
                    if (Directory.Exists(dest))
                        throw Error(StatusCode.InvalidArgument, $"'{dest}' is an existing directory.");
                    var dir = Path.GetDirectoryName(dest);
                    if (string.IsNullOrEmpty(dir))
                        throw Error(StatusCode.InvalidArgument, "destination_path must include a file name.");
                    if (!Directory.Exists(dir))
                        throw Error(StatusCode.NotFound, $"Destination directory '{dir}' does not exist.");
                    if (File.Exists(dest) && !overwrite)
                        throw Error(StatusCode.AlreadyExists, $"'{dest}' already exists (set overwrite to replace it).");

                    temp = Path.Combine(dir, $".homerms-upload-{Guid.NewGuid():N}.part");
                    fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.Asynchronous);
                    log($"[files] '{op}' uploading to '{dest}'{(overwrite ? " (overwrite)" : "")}");
                }

                if (chunk.Data.Length > 0)
                {
                    written += chunk.Data.Length;
                    if (written > MaxUploadBytes)
                        throw Error(StatusCode.ResourceExhausted, $"Upload exceeds the {MaxUploadBytes / (1024 * 1024 * 1024)} GB limit.");
                    await fs!.WriteAsync(chunk.Data.Memory, context.CancellationToken);
                }
            }

            if (dest is null || fs is null || temp is null)
                throw Error(StatusCode.InvalidArgument, "No upload data received.");

            await fs.FlushAsync(context.CancellationToken);
            await fs.DisposeAsync();
            fs = null;

            if (!overwrite && File.Exists(dest))
                throw Error(StatusCode.AlreadyExists, $"'{dest}' already exists (set overwrite to replace it).");
            File.Move(temp, dest, overwrite);
            temp = null;

            log($"[files] '{op}' uploaded '{dest}' ({written:N0} bytes)");
            return new FileOpResult { Success = true, Message = $"Uploaded {written:N0} bytes.", Path = dest, Bytes = written };
        }
        catch (Exception ex) { throw Fail(op, $"upload '{dest ?? "?"}'", ex); }
        finally
        {
            if (fs is not null) await fs.DisposeAsync();
            if (temp is not null)
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    public FileOpResult DeletePath(DeletePathRequest request, string op)
    {
        try
        {
            var full = SafePath.Normalize(request.Path);
            if (SafePath.IsProtected(full))
                throw Error(StatusCode.PermissionDenied, $"Refusing to delete protected location '{full}'.");

            if (File.Exists(full))
            {
                var attrs = File.GetAttributes(full);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(full, attrs & ~FileAttributes.ReadOnly);
                File.Delete(full);
            }
            else if (Directory.Exists(full))
            {
                var isLink = (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0;
                if (!request.Recursive && !isLink && Directory.EnumerateFileSystemEntries(full).Any())
                    throw Error(StatusCode.FailedPrecondition, $"Directory '{full}' is not empty (set recursive to delete it).");
                // A link/junction is removed itself; its target is never traversed.
                Directory.Delete(full, recursive: request.Recursive && !isLink);
            }
            else
            {
                throw Error(StatusCode.NotFound, $"'{full}' does not exist.");
            }

            log($"[files] '{op}' deleted '{full}'{(request.Recursive ? " (recursive)" : "")}");
            return new FileOpResult { Success = true, Message = "Deleted.", Path = full };
        }
        catch (Exception ex) { throw Fail(op, $"delete '{request.Path}'", ex); }
    }

    public FileOpResult RenamePath(RenamePathRequest request, string op)
    {
        try
        {
            var src = SafePath.Normalize(request.SourcePath, "source_path");
            var dst = SafePath.Normalize(request.DestinationPath, "destination_path");
            if (SafePath.IsProtected(src))
                throw Error(StatusCode.PermissionDenied, $"Refusing to rename protected location '{src}'.");
            if (string.Equals(src, dst, StringComparison.Ordinal))
                throw Error(StatusCode.InvalidArgument, "Source and destination are the same.");

            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var caseOnlyRename = string.Equals(src, dst, cmp);
            var dstDir = Path.GetDirectoryName(dst);
            if (string.IsNullOrEmpty(dstDir) || !Directory.Exists(dstDir))
                throw Error(StatusCode.NotFound, $"Destination directory '{dstDir}' does not exist.");

            if (File.Exists(src))
            {
                if (!caseOnlyRename)
                {
                    if (Directory.Exists(dst))
                        throw Error(StatusCode.AlreadyExists, $"A directory named '{dst}' already exists.");
                    if (File.Exists(dst) && !request.Overwrite)
                        throw Error(StatusCode.AlreadyExists, $"'{dst}' already exists (set overwrite to replace it).");
                }
                File.Move(src, dst, request.Overwrite && !caseOnlyRename);
            }
            else if (Directory.Exists(src))
            {
                if (!caseOnlyRename && (Directory.Exists(dst) || File.Exists(dst)))
                    throw Error(StatusCode.AlreadyExists, $"'{dst}' already exists.");
                if (dst.StartsWith(src + Path.DirectorySeparatorChar, cmp))
                    throw Error(StatusCode.InvalidArgument, "Cannot move a directory into itself.");
                Directory.Move(src, dst);
            }
            else
            {
                throw Error(StatusCode.NotFound, $"'{src}' does not exist.");
            }

            log($"[files] '{op}' renamed '{src}' -> '{dst}'");
            return new FileOpResult { Success = true, Message = "Renamed.", Path = dst };
        }
        catch (Exception ex) { throw Fail(op, $"rename '{request.SourcePath}'", ex); }
    }

    public FileOpResult CreateDirectory(FilePathRequest request, string op)
    {
        try
        {
            var full = SafePath.Normalize(request.Path);
            if (File.Exists(full))
                throw Error(StatusCode.AlreadyExists, $"A file named '{full}' already exists.");
            if (Directory.Exists(full))
                return new FileOpResult { Success = true, Message = "Directory already exists.", Path = full };

            Directory.CreateDirectory(full);
            log($"[files] '{op}' created directory '{full}'");
            return new FileOpResult { Success = true, Message = "Created.", Path = full };
        }
        catch (Exception ex) { throw Fail(op, $"create directory '{request.Path}'", ex); }
    }
}
