using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using HomeRms.Shared.Protocol;

namespace HomeRms.Agent.Features;

/// <summary>
/// Low-latency screen streaming for tile-capable controllers.
/// Captures with DXGI Desktop Duplication, compares the changed regions against what the
/// controller already shows, and sends only the 64×64 cells that really changed as JPEG tiles.
/// Cells that changed are re-sent at near-lossless quality (or losslessly as PNG, see
/// <see cref="TileOptions.LosslessRefine"/>) once they have been still for a moment.
/// The pointer is sent as position + shape, so moving the mouse costs almost nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TileStreamer : IDisposable
{
    private const int Cell = 64;
    public const int DefaultRefineDelayMs = 250;
    private const double KeyframeThreshold = 0.55; // above this share of changed cells, send one full frame
    private const int RefineBandRows = 4;           // refine rects are at most 4 cells tall so they encode in parallel

    private readonly Action<string> _log;
    private readonly List<Rectangle> _changedRects = new();
    private readonly ThreadLocal<WicJpegEncoder> _encoders = new(() => new WicJpegEncoder(), trackAllValues: false);
    private readonly ThreadLocal<byte[]> _bgrBuffers = new(() => Array.Empty<byte>(), trackAllValues: false);
    private readonly List<int> _sendNow = new();
    private readonly List<int> _refine = new();
    private readonly HashSet<int> _candidates = new();
    private bool[] _marked = Array.Empty<bool>();
    private bool[] _inSendNow = Array.Empty<bool>();

    private DesktopDuplicator? _dup;
    private long _nextRetryTick;
    private byte[] _sent = Array.Empty<byte>();   // what the controller currently shows (BGRA)
    private int _cols, _rows;
    private bool[] _pendingRefine = Array.Empty<bool>();
    private long[] _changedTick = Array.Empty<long>();
    private bool _needKeyframe = true;

    private readonly CursorTracker _cursor = new();

    public TileStreamer(Action<string> log) => _log = log;

    /// <summary>True while GPU capture is working (false = caller should use the GDI path).</summary>
    public bool Available => EnsureDuplicator();

    public int Width => _dup?.Width ?? 0;
    public int Height => _dup?.Height ?? 0;

    /// <summary>
    /// Waits up to <paramref name="waitMs"/> for screen or pointer changes and returns the update
    /// to send, or null if nothing changed. Throws if GPU capture is lost and cannot be restored.
    /// </summary>
    public ScreenFrame? Next(int quality, int waitMs) => Next(new TileOptions(quality), waitMs);

    /// <inheritdoc cref="Next(int, int)"/>
    public ScreenFrame? Next(TileOptions o, int waitMs)
    {
        if (!EnsureDuplicator())
            return null;

        var dup = _dup!;
        bool imageChanged;
        try
        {
            imageChanged = dup.TryAcquire(waitMs, _changedRects);
        }
        catch (Exception ex)
        {
            // Desktop switch (UAC / lock screen), resolution change, driver reset…
            _log($"[screen] GPU capture lost ({ex.Message}); reopening");
            ResetDuplicator();
            return null;
        }

        var now = Environment.TickCount64;
        var quality = Math.Clamp(o.Quality, 1, 100);
        var refineQuality = Math.Max(quality, ScreenCapturer.RefineQuality);
        // Lossy refine is pointless once the motion quality already reaches it; lossless always improves
        var needsRefine = o.LosslessRefine || quality < refineQuality;
        var refineAfterMs = o.RefineDelayMs > 0 ? Math.Clamp(o.RefineDelayMs, 30, 10_000) : DefaultRefineDelayMs;

        if (_needKeyframe)
        {
            if (!imageChanged) return null; // wait for the first desktop image
            var keyframe = NewFrame(dup);
            keyframe.Jpeg = Wrap(EncodeRect(dup, new Rectangle(0, 0, dup.Width, dup.Height), quality, o.FullChroma, png: false));
            Buffer.BlockCopy(dup.Pixels, 0, _sent, 0, _sent.Length);
            Array.Fill(_pendingRefine, needsRefine);
            Array.Fill(_changedTick, now);
            _needKeyframe = false;
            keyframe.Cursor = _cursor.Read(dup, forceShape: true);
            return keyframe;
        }

        // 1. Which cells really differ from what the controller shows?
        _sendNow.Clear();
        if (imageChanged)
        {
            _candidates.Clear();
            foreach (var r in _changedRects)
            {
                for (var cy = r.Top / Cell; cy <= (r.Bottom - 1) / Cell; cy++)
                    for (var cx = r.Left / Cell; cx <= (r.Right - 1) / Cell; cx++)
                        _candidates.Add(cy * _cols + cx);
            }
            foreach (var c in _candidates)
            {
                if (CellDiffers(dup, c))
                    _sendNow.Add(c);
            }
        }

        // 2. Many cells changed (video, full-screen scroll): one full frame is cheaper than tiles
        if (_sendNow.Count > _cols * _rows * KeyframeThreshold)
        {
            var full = NewFrame(dup);
            full.Jpeg = Wrap(EncodeRect(dup, new Rectangle(0, 0, dup.Width, dup.Height), quality, o.FullChroma, png: false));
            Buffer.BlockCopy(dup.Pixels, 0, _sent, 0, _sent.Length);
            foreach (var c in _sendNow) { _pendingRefine[c] = needsRefine; _changedTick[c] = now; }
            full.Cursor = _cursor.Read(dup, forceShape: false);
            return full;
        }

        // 3. Cells that have been still long enough get a sharp (or lossless) re-send
        foreach (var c in _sendNow) _inSendNow[c] = true;
        _refine.Clear();
        for (var c = 0; c < _pendingRefine.Length; c++)
        {
            if (_pendingRefine[c] && !_inSendNow[c] && now - _changedTick[c] >= refineAfterMs)
                _refine.Add(c);
        }
        foreach (var c in _sendNow) _inSendNow[c] = false;

        var tiles = new List<(Rectangle Rect, int Quality, bool Png)>();
        foreach (var r in MergeCells(_sendNow, dup, maxRows: int.MaxValue / Cell)) tiles.Add((r, quality, false));
        foreach (var r in MergeCells(_refine, dup, maxRows: RefineBandRows)) tiles.Add((r, refineQuality, o.LosslessRefine));

        var frame = NewFrame(dup);

        // 4. Encode tiles in parallel (each worker thread keeps its own WIC encoder and pixel buffer)
        if (tiles.Count > 0)
        {
            var encoded = new byte[tiles.Count][];
            if (tiles.Count == 1)
                encoded[0] = EncodeRect(dup, tiles[0].Rect, tiles[0].Quality, o.FullChroma, tiles[0].Png);
            else
                Parallel.For(0, tiles.Count, i => encoded[i] = EncodeRect(dup, tiles[i].Rect, tiles[i].Quality, o.FullChroma, tiles[i].Png));
            for (var i = 0; i < tiles.Count; i++)
            {
                var r = tiles[i].Rect;
                frame.Tiles.Add(new ScreenTile
                {
                    X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                    Jpeg = Wrap(encoded[i]),
                    Format = tiles[i].Png ? 1 : 0
                });
            }
        }

        foreach (var c in _sendNow)
        {
            CopyCellToSent(dup, c);
            _pendingRefine[c] = needsRefine;
            _changedTick[c] = now;
        }
        foreach (var c in _refine) _pendingRefine[c] = false;

        var cursor = _cursor.Read(dup, forceShape: false);
        if (frame.Tiles.Count == 0 && !_cursor.Changed)
            return null;

        frame.Cursor = cursor;
        return frame;
    }

    private static ScreenFrame NewFrame(DesktopDuplicator dup) =>
        new() { Width = dup.Width, Height = dup.Height, CaptureUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

    // The encoder hands over a fresh array that nothing else touches: no need to copy it again
    private static Google.Protobuf.ByteString Wrap(byte[] bytes) => Google.Protobuf.UnsafeByteOperations.UnsafeWrap(bytes);

    private bool EnsureDuplicator()
    {
        if (_dup is not null) return true;
        var now = Environment.TickCount64;
        if (now < _nextRetryTick) return false;

        _dup = DesktopDuplicator.TryCreate(out var error);
        if (_dup is null)
        {
            _nextRetryTick = now + 1000;
            _log($"[screen] GPU capture unavailable ({error}); using GDI capture");
            return false;
        }

        _cols = (_dup.Width + Cell - 1) / Cell;
        _rows = (_dup.Height + Cell - 1) / Cell;
        _sent = new byte[_dup.Pixels.Length];
        _pendingRefine = new bool[_cols * _rows];
        _changedTick = new long[_cols * _rows];
        _marked = new bool[_cols * _rows];
        _inSendNow = new bool[_cols * _rows];
        _needKeyframe = true;
        _log($"[screen] GPU capture active ({_dup.Width}×{_dup.Height}, dirty-tile streaming)");
        return true;
    }

    private void ResetDuplicator()
    {
        _dup?.Dispose();
        _dup = null;
        _needKeyframe = true;
    }

    private Rectangle CellRect(DesktopDuplicator dup, int c)
    {
        var x = c % _cols * Cell;
        var y = c / _cols * Cell;
        return new Rectangle(x, y, Math.Min(Cell, dup.Width - x), Math.Min(Cell, dup.Height - y));
    }

    private bool CellDiffers(DesktopDuplicator dup, int c)
    {
        var r = CellRect(dup, c);
        var stride = dup.Stride;
        var len = r.Width * 4;
        var cur = dup.Pixels.AsSpan();
        var sent = _sent.AsSpan();
        for (var y = r.Top; y < r.Bottom; y++)
        {
            var off = y * stride + r.Left * 4;
            if (!cur.Slice(off, len).SequenceEqual(sent.Slice(off, len)))
                return true;
        }
        return false;
    }

    private void CopyCellToSent(DesktopDuplicator dup, int c)
    {
        var r = CellRect(dup, c);
        var stride = dup.Stride;
        var len = r.Width * 4;
        for (var y = r.Top; y < r.Bottom; y++)
        {
            var off = y * stride + r.Left * 4;
            Buffer.BlockCopy(dup.Pixels, off, _sent, off, len);
        }
    }

    /// <summary>
    /// Joins marked cells into horizontal runs, then stacks runs with the same span
    /// (at most <paramref name="maxRows"/> cells tall).
    /// </summary>
    private List<Rectangle> MergeCells(List<int> cells, DesktopDuplicator dup, int maxRows)
    {
        var result = new List<Rectangle>();
        if (cells.Count == 0) return result;

        var marked = _marked;
        foreach (var c in cells) marked[c] = true;

        var open = new Dictionary<(int X0, int X1), int>(); // span -> index in result (rect ending at previous row)
        for (var row = 0; row < _rows; row++)
        {
            var nextOpen = new Dictionary<(int, int), int>();
            var col = 0;
            while (col < _cols)
            {
                if (!marked[row * _cols + col]) { col++; continue; }
                var start = col;
                while (col < _cols && marked[row * _cols + col]) col++;
                var span = (start, col);

                var top = row * Cell;
                var bottom = Math.Min(dup.Height, top + Cell);
                var left = start * Cell;
                var right = Math.Min(dup.Width, col * Cell);

                if (open.TryGetValue(span, out var idx) && result[idx].Height < maxRows * Cell)
                {
                    var prev = result[idx];
                    result[idx] = Rectangle.FromLTRB(prev.Left, prev.Top, prev.Right, bottom);
                    nextOpen[span] = idx;
                }
                else
                {
                    result.Add(Rectangle.FromLTRB(left, top, right, bottom));
                    nextOpen[span] = result.Count - 1;
                }
            }
            open = nextOpen;
        }
        foreach (var c in cells) marked[c] = false;
        return result;
    }

    /// <summary>Encodes part of the BGRA desktop image as JPEG (4:4:4 or 4:2:0) or as a lossless PNG.</summary>
    private byte[] EncodeRect(DesktopDuplicator dup, Rectangle r, int quality, bool fullChroma, bool png)
    {
        var stride = (r.Width * 3 + 3) & ~3;
        var bgr = _bgrBuffers.Value!;
        if (bgr.Length < stride * r.Height)
            _bgrBuffers.Value = bgr = new byte[stride * r.Height];
        var src = dup.Pixels;
        var srcStride = dup.Stride;
        for (var y = 0; y < r.Height; y++)
        {
            var s = (r.Top + y) * srcStride + r.Left * 4;
            var d = y * stride;
            for (var x = 0; x < r.Width; x++, s += 4, d += 3)
            {
                bgr[d] = src[s];
                bgr[d + 1] = src[s + 1];
                bgr[d + 2] = src[s + 2];
            }
        }
        var encoder = _encoders.Value!;
        return png
            ? encoder.EncodePng(bgr, r.Width, r.Height, stride)
            : encoder.Encode(bgr, r.Width, r.Height, stride, quality, fullChroma);
    }

    public void Dispose()
    {
        _dup?.Dispose();
        _encoders.Dispose();
        _bgrBuffers.Dispose();
    }
}

/// <summary>Per-stream encoding choices for <see cref="TileStreamer"/>.</summary>
/// <param name="Quality">JPEG quality for changed regions (1-100).</param>
/// <param name="FullChroma">true = 4:4:4 JPEG, false = 4:2:0.</param>
/// <param name="LosslessRefine">Re-send still regions as PNG instead of high-quality JPEG.</param>
/// <param name="RefineDelayMs">How long a region must be still before the refine pass (0 = default).</param>
internal readonly record struct TileOptions(int Quality, bool FullChroma = true, bool LosslessRefine = false, int RefineDelayMs = 0);

/// <summary>Reads the system pointer position/shape and turns the shape into a PNG with alpha.</summary>
[SupportedOSPlatform("windows")]
internal sealed class CursorTracker
{
    private IntPtr _lastHandle = IntPtr.Zero;
    private long _shapeId;
    private int _hotX, _hotY;
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private bool _lastVisible;

    /// <summary>True when the last <see cref="Read"/> differs from the one before.</summary>
    public bool Changed { get; private set; }

    public ScreenCursor Read(DesktopDuplicator dup, bool forceShape)
    {
        var cursor = new ScreenCursor();
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        var visible = GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero;
        var x = ci.ptScreenPos.x - dup.OffsetX;
        var y = ci.ptScreenPos.y - dup.OffsetY;

        var shapeChanged = false;
        if (visible && (forceShape || ci.hCursor != _lastHandle))
        {
            var png = RenderShape(ci.hCursor, out var hx, out var hy);
            if (png is not null)
            {
                _lastHandle = ci.hCursor;
                _shapeId++;
                _hotX = hx;
                _hotY = hy;
                cursor.ShapePng = Google.Protobuf.ByteString.CopyFrom(png);
                shapeChanged = true;
            }
        }

        Changed = shapeChanged || visible != _lastVisible || x != _lastX || y != _lastY;
        _lastVisible = visible;
        _lastX = x;
        _lastY = y;

        cursor.Visible = visible;
        cursor.X = x;
        cursor.Y = y;
        cursor.HotspotX = _hotX;
        cursor.HotspotY = _hotY;
        cursor.ShapeId = _shapeId;
        return cursor;
    }

    /// <summary>Draws the cursor on black and on white to recover per-pixel alpha (works for mono and colour cursors).</summary>
    private static byte[]? RenderShape(IntPtr hCursor, out int hotX, out int hotY)
    {
        hotX = hotY = 0;
        if (!GetIconInfo(hCursor, out var info)) return null;
        try
        {
            hotX = info.xHotspot;
            hotY = info.yHotspot;
            int w, h;
            using (var mask = Image.FromHbitmap(info.hbmMask))
            {
                w = mask.Width;
                h = info.hbmColor == IntPtr.Zero ? mask.Height / 2 : mask.Height;
            }
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            using var onBlack = DrawOn(hCursor, w, h, Color.Black);
            using var onWhite = DrawOn(hCursor, w, h, Color.White);
            using var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (var py = 0; py < h; py++)
            {
                for (var px = 0; px < w; px++)
                {
                    var b = onBlack.GetPixel(px, py);
                    var wh = onWhite.GetPixel(px, py);
                    var alpha = 255 - Math.Max(wh.R - b.R, Math.Max(wh.G - b.G, wh.B - b.B));
                    alpha = Math.Clamp(alpha, 0, 255);
                    if (alpha == 0) continue;
                    int Un(int v) => Math.Clamp(v * 255 / alpha, 0, 255);
                    result.SetPixel(px, py, Color.FromArgb(alpha, Un(b.R), Un(b.G), Un(b.B)));
                }
            }

            using var ms = new MemoryStream();
            result.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
    }

    private static Bitmap DrawOn(IntPtr hCursor, int w, int h, Color background)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(background);
        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
        return bmp;
    }

    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
