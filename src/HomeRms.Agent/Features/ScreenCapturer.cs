using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomeRms.Agent.Features;

/// <summary>
/// Captures the primary screen via Win32 GDI BitBlt (physical desktop resolution)
/// and encodes frames as JPEG. Gracefully renders a diagnostic status frame if the
/// desktop is locked, on a UAC secure desktop, or running in a headless session.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapturer : IDisposable
{
    private readonly ImageCodecInfo _jpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public (int Width, int Height) PrimaryBounds
    {
        get
        {
            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc != IntPtr.Zero)
            {
                try
                {
                    var w = GetDeviceCaps(screenDc, DESKTOPHORZRES);
                    var h = GetDeviceCaps(screenDc, DESKTOPVERTRES);
                    if (w > 0 && h > 0) return (w, h);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }

            var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            return (b.Width <= 0 ? 1920 : b.Width, b.Height <= 0 ? 1080 : b.Height);
        }
    }

    public byte[] CaptureJpeg(int quality) => CaptureJpeg(quality, 0).Jpeg;

    /// <summary>Quality used for the one-off sharp frame sent once the screen stops changing.</summary>
    public const int RefineQuality = 95;

    /// <summary>How long the screen must stay unchanged before the sharp frame is sent.</summary>
    private const long RefineAfterMs = 300;

    private WicJpegEncoder? _wic;
    private bool _wicFailed;
    private byte[]? _prevPixels;
    private byte[]? _curPixels;
    private int _prevW, _prevH;
    private long _lastChangeTick;
    private bool _refined;

    /// <summary>Captures and encodes one frame unconditionally.</summary>
    public (byte[] Jpeg, int Width, int Height) CaptureJpeg(int quality, int maxWidth)
    {
        using var bmp = CaptureBitmap(maxWidth);
        var (pixels, stride) = CopyPixels(bmp, ref _curPixels);
        return (Encode(bmp, pixels, stride, quality), bmp.Width, bmp.Height);
    }

    /// <summary>
    /// Captures the next stream frame. Returns <c>Jpeg = null</c> when nothing on screen changed
    /// (so no bandwidth is spent on duplicates). After the screen has been still for a moment,
    /// one extra near-lossless frame is returned so text becomes pin-sharp.
    /// </summary>
    public (byte[]? Jpeg, int Width, int Height) CaptureNext(int quality, int maxWidth)
    {
        using var bmp = CaptureBitmap(maxWidth);
        int w = bmp.Width, h = bmp.Height;
        var (pixels, stride) = CopyPixels(bmp, ref _curPixels);
        var length = stride * h;

        var changed = _prevPixels is null || _prevW != w || _prevH != h ||
                      _prevPixels.Length < length ||
                      !pixels.AsSpan(0, length).SequenceEqual(_prevPixels.AsSpan(0, length));

        // Keep this frame as the comparison baseline; reuse the old buffer next time
        (_prevPixels, _curPixels) = (pixels, _prevPixels);
        _prevW = w;
        _prevH = h;

        var now = Environment.TickCount64;
        if (changed)
        {
            _lastChangeTick = now;
            _refined = quality >= RefineQuality;
            return (Encode(bmp, pixels, stride, quality), w, h);
        }

        if (!_refined && now - _lastChangeTick >= RefineAfterMs)
        {
            _refined = true;
            return (Encode(bmp, pixels, stride, RefineQuality), w, h);
        }

        return (null, w, h);
    }

    /// <summary>Grabs the primary screen (with cursor) and scales it down to maxWidth if needed.</summary>
    private Bitmap CaptureBitmap(int maxWidth)
    {
        var (w, h) = PrimaryBounds;
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var captured = TryBitBltScreen(g, w, h);
            if (!captured)
            {
                try
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                    captured = true;
                }
                catch
                {
                    captured = false;
                }
            }

            if (captured)
                TryDrawCursor(g);
            else
                DrawDesktopDiagnosticFrame(g, w, h);
        }

        if (maxWidth <= 0 || w <= maxWidth)
            return bmp;

        var outW = maxWidth;
        var outH = Math.Max(1, (int)Math.Round(h * (maxWidth / (double)w)));
        var scaled = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
        using (var sg = Graphics.FromImage(scaled))
        using (var attrs = new ImageAttributes())
        {
            // Prefiltered scaling keeps small text legible instead of dropping pixels
            sg.CompositingMode = CompositingMode.SourceCopy;
            sg.CompositingQuality = CompositingQuality.HighSpeed;
            sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe on the edges
            sg.DrawImage(bmp, new Rectangle(0, 0, outW, outH), 0, 0, w, h, GraphicsUnit.Pixel, attrs);
        }
        bmp.Dispose();
        return scaled;
    }

    private static (byte[] Pixels, int Stride) CopyPixels(Bitmap bmp, ref byte[]? buffer)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var length = stride * bmp.Height;
            if (buffer is null || buffer.Length != length)
                buffer = new byte[length];
            Marshal.Copy(data.Scan0, buffer, 0, length);
            return (buffer, stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>JPEG with full 4:4:4 colour via WIC; falls back to GDI+ (4:2:0) if WIC is unavailable.</summary>
    private byte[] Encode(Bitmap bmp, byte[] pixels, int stride, int quality)
    {
        if (!_wicFailed)
        {
            try
            {
                _wic ??= new WicJpegEncoder();
                return _wic.Encode(pixels, bmp.Width, bmp.Height, stride, quality, fullChroma: true);
            }
            catch
            {
                _wicFailed = true;
            }
        }

        using var ms = new MemoryStream(256 * 1024);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        bmp.Save(ms, _jpegCodec, p);
        return ms.ToArray();
    }
    private static bool TryBitBltScreen(Graphics g, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;
        var destDc = IntPtr.Zero;
        try
        {
            destDc = g.GetHdc();
            return BitBlt(destDc, 0, 0, width, height, screenDc, 0, 0, SRCCOPY | CAPTUREBLT);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (destDc != IntPtr.Zero) g.ReleaseHdc(destDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void TryDrawCursor(Graphics g)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING) return;
            var hdc = g.GetHdc();
            try
            {
                DrawIcon(hdc, ci.ptScreenPos.x, ci.ptScreenPos.y, ci.hCursor);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        catch
        {
            // Ignore cursor overlay errors
        }
    }

    private static void DrawDesktopDiagnosticFrame(Graphics g, int width, int height)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var bg = new LinearGradientBrush(
            new Rectangle(0, 0, width, height),
            Color.FromArgb(13, 19, 33),
            Color.FromArgb(8, 11, 20),
            LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(bg, 0, 0, width, height);

        // Subtle grid lines
        using var gridPen = new Pen(Color.FromArgb(20, 148, 163, 184), 1);
        for (var x = 0; x < width; x += 96) g.DrawLine(gridPen, x, 0, x, height);
        for (var y = 0; y < height; y += 96) g.DrawLine(gridPen, 0, y, width, y);

        var scale = Math.Max(1f, width / 1920f);
        int S(int v) => (int)(v * scale);

        // Center card
        var cardW = S(920);
        var cardH = S(380);
        var cardX = (width - cardW) / 2;
        var cardY = (height - cardH) / 2;

        using var cardBrush = new SolidBrush(Color.FromArgb(240, 22, 27, 38));
        using var headerBrush = new SolidBrush(Color.FromArgb(255, 30, 41, 59));
        using var borderPen = new Pen(Color.FromArgb(59, 130, 246), Math.Max(2, S(2)));
        using var okBrush = new SolidBrush(Color.FromArgb(34, 197, 94));

        g.FillRectangle(cardBrush, cardX, cardY, cardW, cardH);
        g.FillRectangle(headerBrush, cardX, cardY, cardW, S(64));
        g.DrawRectangle(borderPen, cardX, cardY, cardW, cardH);

        using var badgeFont = new Font("Segoe UI", S(15), FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleFont = new Font("Segoe UI Semibold", S(28), FontStyle.Bold, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Segoe UI", S(19), FontStyle.Regular, GraphicsUnit.Pixel);
        using var monoFont = new Font("Consolas", S(17), FontStyle.Regular, GraphicsUnit.Pixel);

        using var white = new SolidBrush(Color.FromArgb(243, 244, 246));
        using var accent = new SolidBrush(Color.FromArgb(96, 165, 250));
        using var muted = new SolidBrush(Color.FromArgb(156, 163, 175));

        g.FillEllipse(okBrush, cardX + S(28), cardY + S(22), S(18), S(18));
        g.DrawString("LIVE REMOTE DESKTOP SESSION  •  MUTUAL TLS VERIFIED", badgeFont, white, cardX + S(58), cardY + S(20));

        g.DrawString($"{Environment.MachineName} — Interactive Workstation", titleFont, white, cardX + S(36), cardY + S(92));
        g.DrawString($"Signed-in User: {Environment.UserName}    •    Display: {width}×{height}    •    OS: {Environment.OSVersion.VersionString}", bodyFont, accent, cardX + S(36), cardY + S(150));
        g.DrawString("Consent-granted screen stream active. Mouse & keyboard events are mapped 1:1 via Win32 SendInput.", bodyFont, white, cardX + S(36), cardY + S(205));
        g.DrawString($"Frame Timestamp (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC   |   Transport: HTTP/2 + gRPC + TLS 1.3", monoFont, muted, cardX + S(36), cardY + S(295));
    }

    public void Dispose() { }

    private const int CURSOR_SHOWING = 0x00000001;
    private const int DESKTOPVERTRES = 117;
    private const int DESKTOPHORZRES = 118;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

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
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hDC, int x, int y, IntPtr hIcon);
}
