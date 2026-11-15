using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeRms.Agent.Features;

/// <summary>Captures the primary screen via GDI BitBlt and encodes frames as JPEG.</summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapturer : IDisposable
{
    private readonly ImageCodecInfo _jpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public (int Width, int Height) PrimaryBounds
    {
        get
        {
            var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            return (b.Width, b.Height);
        }
    }

    public byte[] CaptureJpeg(int quality)
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(g, bounds);
        }

        using var ms = new MemoryStream();
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        bmp.Save(ms, _jpegCodec, p);
        return ms.ToArray();
    }

    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING) return;
        DrawIcon(g.GetHdc(), ci.ptScreenPos.x - bounds.Left, ci.ptScreenPos.y - bounds.Top, ci.hCursor);
        g.ReleaseHdc();
    }

    public void Dispose() { }

    private const int CURSOR_SHOWING = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hDC, int x, int y, IntPtr hIcon);
}
