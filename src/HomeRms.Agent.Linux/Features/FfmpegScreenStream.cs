using System.Diagnostics;

namespace HomeRms.Agent.Linux.Features;

/// <summary>
/// Continuous X11 screen capture: one long-running ffmpeg (x11grab → MJPEG on stdout) instead of
/// starting a screenshot tool for every frame. Frames are split on JPEG start/end markers and the
/// newest one is kept, so a slow network never makes the picture lag behind.
/// Encodes with full 4:4:4 colour so coloured text stays sharp.
/// </summary>
public sealed class FfmpegScreenStream : IDisposable
{
    private readonly Process _proc;
    private readonly Thread _reader;
    private readonly object _gate = new();
    private byte[]? _latest;
    private long _latestSeq;
    private volatile bool _stopped;

    public int Width { get; }
    public int Height { get; }

    private FfmpegScreenStream(Process proc, int width, int height)
    {
        _proc = proc;
        Width = width;
        Height = height;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ffmpeg-mjpeg-reader" };
        _reader.Start();
    }

    /// <summary>Starts capture on X11, or returns null if this is not an X11 session or ffmpeg is missing.</summary>
    public static FfmpegScreenStream? TryStart(int fps, int quality, int maxWidth, int screenW, int screenH)
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (string.IsNullOrEmpty(display) || wayland || !CommandExists("ffmpeg"))
            return null;

        // ffmpeg -q:v: 2 = best … 31 = worst
        var q = Math.Clamp((int)Math.Round((100 - Math.Clamp(quality, 20, 95)) / 4.0) + 1, 2, 31);
        var outW = screenW;
        var outH = screenH;
        var scale = "";
        if (maxWidth > 0 && screenW > maxWidth)
        {
            outW = maxWidth;
            outH = (int)Math.Round(screenH * (maxWidth / (double)screenW)) / 2 * 2;
            scale = $"-vf scale={outW}:{outH}:flags=bicubic ";
        }

        var args = $"-loglevel error -nostdin -f x11grab -draw_mouse 1 -framerate {Math.Clamp(fps, 1, 60)} " +
                   $"-video_size {screenW}x{screenH} -i {display} {scale}" +
                   $"-pix_fmt yuvj444p -c:v mjpeg -q:v {q} -f image2pipe -";
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null) return null;
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            return new FfmpegScreenStream(proc, outW, outH);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Newest frame and its number (frames with the same number were already returned).</summary>
    public (byte[]? Jpeg, long Seq) Latest
    {
        get { lock (_gate) return (_latest, _latestSeq); }
    }

    public bool HasExited => _stopped || _proc.HasExited;

    private void ReadLoop()
    {
        var stream = _proc.StandardOutput.BaseStream;
        var buf = new byte[1 << 20];
        var frame = new MemoryStream(1 << 20);
        var inFrame = false;
        byte prev = 0;
        try
        {
            int n;
            while (!_stopped && (n = stream.Read(buf, 0, buf.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var b = buf[i];
                    if (!inFrame)
                    {
                        if (prev == 0xFF && b == 0xD8) // SOI
                        {
                            frame.SetLength(0);
                            frame.WriteByte(0xFF);
                            frame.WriteByte(0xD8);
                            inFrame = true;
                        }
                    }
                    else
                    {
                        frame.WriteByte(b);
                        if (prev == 0xFF && b == 0xD9) // EOI (0xFF in entropy data is always byte-stuffed)
                        {
                            var jpeg = frame.ToArray();
                            lock (_gate)
                            {
                                _latest = jpeg;
                                _latestSeq++;
                            }
                            inFrame = false;
                            b = 0;
                        }
                    }
                    prev = b;
                }
            }
        }
        catch
        {
            // process ended
        }
        finally
        {
            _stopped = true;
        }
    }

    private static bool CommandExists(string cmd)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        return path.Split(':').Any(dir => File.Exists(Path.Combine(dir, cmd)));
    }

    public void Dispose()
    {
        _stopped = true;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _proc.Dispose();
    }
}
