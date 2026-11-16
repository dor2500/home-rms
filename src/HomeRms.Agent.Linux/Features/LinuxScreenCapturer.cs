using System.Diagnostics;

namespace HomeRms.Agent.Linux.Features;

/// <summary>
/// Captures the Linux desktop (Wayland via grim, or X11 via scrot/import/ffmpeg)
/// and returns JPEG bytes + resolution. Falls back to a minimal valid JPEG if headless.
/// </summary>
public sealed class LinuxScreenCapturer : IDisposable
{
    private readonly string? _tool;
    private (int W, int H) _cachedBounds = (1920, 1080);

    public LinuxScreenCapturer()
    {
        _tool = DetectCaptureTool();
        _cachedBounds = QueryDisplayBounds();
    }

    public (int Width, int Height) PrimaryBounds => _cachedBounds;

    public (byte[] Jpeg, int Width, int Height) CaptureJpeg(int quality, int maxWidth)
    {
        quality = Math.Clamp(quality, 15, 95);
        var tmp = Path.Combine(Path.GetTempPath(), $"hrms_cap_{Guid.NewGuid():N}.jpg");
        try
        {
            var captured = _tool switch
            {
                "grim" => RunTool("grim", $"-t jpeg -q {quality} \"{tmp}\""),
                "scrot" => RunTool("scrot", $"-q {quality} -o \"{tmp}\""),
                "import" => RunTool("import", $"-window root -quality {quality} \"{tmp}\""),
                "ffmpeg" => RunTool("ffmpeg", $"-y -f x11grab -video_size {_cachedBounds.W}x{_cachedBounds.H} -i {Environment.GetEnvironmentVariable("DISPLAY") ?? ":0"} -vframes 1 -q:v {Math.Clamp(31 - quality * 30 / 100, 2, 31)} \"{tmp}\""),
                _ => false
            };

            if (captured && File.Exists(tmp))
            {
                var bytes = File.ReadAllBytes(tmp);
                if (bytes.Length > 100)
                {
                    if (TryReadJpegDimensions(bytes, out var w, out var h))
                        _cachedBounds = (w, h);
                    return (bytes, _cachedBounds.W, _cachedBounds.H);
                }
            }
        }
        catch { }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        // Minimal 1x1 dark JPEG fallback for headless Linux servers without a display server
        return (HeadlessFallbackJpeg, 1280, 720);
    }

    private static string? DetectCaptureTool()
    {
        var isWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var isX11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        if (isWayland && CommandExists("grim")) return "grim";
        if (isX11)
        {
            if (CommandExists("scrot")) return "scrot";
            if (CommandExists("import")) return "import";
            if (CommandExists("ffmpeg")) return "ffmpeg";
        }
        return null;
    }

    private static (int W, int H) QueryDisplayBounds()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "xdpyinfo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is not null)
            {
                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(1000);
                foreach (var line in text.Split('\n'))
                {
                    if (line.Contains("dimensions:", StringComparison.OrdinalIgnoreCase))
                    {
                        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var dim = tokens.FirstOrDefault(t => t.Contains('x') && char.IsDigit(t[0]));
                        if (dim is not null)
                        {
                            var xy = dim.Split('x');
                            if (xy.Length == 2 && int.TryParse(xy[0], out var w) && int.TryParse(xy[1], out var h))
                                return (w, h);
                        }
                    }
                }
            }
        }
        catch { }
        return (1920, 1080);
    }

    private static bool TryReadJpegDimensions(byte[] data, out int width, out int height)
    {
        width = height = 0;
        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF) break;
            var marker = data[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                height = (data[i + 5] << 8) | data[i + 6];
                width = (data[i + 7] << 8) | data[i + 8];
                return width > 0 && height > 0;
            }
            var len = (data[i + 2] << 8) | data[i + 3];
            if (len < 2) break;
            i += 2 + len;
        }
        return false;
    }

    private static bool CommandExists(string cmd)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = cmd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(1000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool RunTool(string tool, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Valid minimal 1x1 dark-slate baseline JPEG
    private static readonly byte[] HeadlessFallbackJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////" +
        "wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=");

    public void Dispose() { }
}
