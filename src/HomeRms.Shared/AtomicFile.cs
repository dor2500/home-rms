using System.Text;

namespace HomeRms.Shared;

/// <summary>
/// Crash-safe settings persistence: content is written to a temp file in the same directory,
/// flushed to disk and then moved over the target, so a crash or power loss mid-write never
/// leaves a truncated (unreadable) settings file behind.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, bool privateOnly = false) =>
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents), privateOnly);

    public static void WriteAllBytes(string path, byte[] bytes, bool privateOnly = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (privateOnly && !OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Copies an unreadable settings file aside (<c>name.corrupt-yyyyMMdd-HHmmss</c>) before it gets
    /// replaced by defaults, so the user's data can still be recovered by hand. Returns the backup
    /// path, or null when the file does not exist or could not be copied.
    /// </summary>
    public static string? PreserveCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
            return backup;
        }
        catch
        {
            return null;
        }
    }
}
