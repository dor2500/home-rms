using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using HomeRms.Shared.Protocol;
using SkiaSharp;

namespace HomeRms.Controller.Mac;

/// <summary>
/// Remote-screen rendering for the macOS/Linux controller: one WriteableBitmap the size of the
/// remote desktop, painted with full frames or dirty tiles, plus the remote pointer drawn separately.
/// </summary>
public partial class MainWindow
{
    private WriteableBitmap? _canvas;
    private int _uiQueued;

    private long _cursorShapeId = -1;
    private Bitmap? _cursorImage;
    private Cursor? _cursorForControl;
    private bool _cursorVisible;
    private int _cursorX, _cursorY, _cursorHotX, _cursorHotY;

    private sealed class ScreenUpdate
    {
        public int Width;
        public int Height;
        public readonly List<Region> Regions = new();
        public ScreenCursor? Cursor;
        public byte[]? CursorPng;

        public sealed record Region(int X, int Y, int W, int H, byte[] Pixels, int Stride);

        public static ScreenUpdate Decode(ScreenFrame frame)
        {
            var u = new ScreenUpdate { Width = frame.Width, Height = frame.Height, Cursor = frame.Cursor };

            if (!frame.Jpeg.IsEmpty)
            {
                var full = DecodeRegion(frame.Jpeg.ToByteArray(), 0, 0);
                if (full is not null)
                {
                    u.Regions.Add(full);
                    if (frame.Tiles.Count == 0) { u.Width = full.W; u.Height = full.H; }
                }
            }

            if (frame.Tiles.Count > 0)
            {
                var decoded = new Region?[frame.Tiles.Count];
                Parallel.For(0, frame.Tiles.Count, i =>
                {
                    var t = frame.Tiles[i];
                    decoded[i] = DecodeRegion(t.Jpeg.ToByteArray(), t.X, t.Y);
                });
                foreach (var r in decoded)
                    if (r is not null) u.Regions.Add(r);
            }

            if (frame.Cursor is { ShapePng.IsEmpty: false } c)
                u.CursorPng = c.ShapePng.ToByteArray();

            return u;
        }

        /// <summary>Decodes a JPEG straight to BGRA so it can be copied into the canvas row by row.</summary>
        private static Region? DecodeRegion(byte[] jpeg, int x, int y)
        {
            using var data = SKData.CreateCopy(jpeg);
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var pixels = new byte[info.RowBytes * info.Height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
            }
            finally
            {
                handle.Free();
            }
            return new Region(x, y, info.Width, info.Height, pixels, info.RowBytes);
        }
    }

    private void ApplyScreenUpdate(ScreenUpdate u)
    {
        if (u.Width <= 0 || u.Height <= 0) return;

        if (u.Regions.Count > 0)
        {
            if (_canvas is null || _canvas.PixelSize.Width != u.Width || _canvas.PixelSize.Height != u.Height)
            {
                _canvas = new WriteableBitmap(new PixelSize(u.Width, u.Height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                ScreenImage.Source = _canvas;
            }

            _frameWidth = u.Width;
            _frameHeight = u.Height;
            DesktopOverlay.IsVisible = false;

            using (var fb = _canvas.Lock())
            {
                foreach (var r in u.Regions)
                {
                    var w = Math.Min(r.W, u.Width - r.X);
                    var h = Math.Min(r.H, u.Height - r.Y);
                    if (w <= 0 || h <= 0 || r.X < 0 || r.Y < 0) continue;
                    for (var row = 0; row < h; row++)
                    {
                        var dst = fb.Address + (r.Y + row) * fb.RowBytes + r.X * 4;
                        Marshal.Copy(r.Pixels, row * r.Stride, dst, w * 4);
                    }
                }
            }
            ScreenImage.InvalidateVisual();
        }

        if (u.Cursor is { } c)
        {
            if (u.CursorPng is not null && c.ShapeId != _cursorShapeId)
            {
                try
                {
                    using var ms = new MemoryStream(u.CursorPng);
                    var bmp = new Bitmap(ms);
                    _cursorImage?.Dispose();
                    _cursorImage = bmp;
                    _cursorForControl?.Dispose();
                    _cursorForControl = new Cursor(bmp, new PixelPoint(c.HotspotX, c.HotspotY));
                    _cursorShapeId = c.ShapeId;
                    RemoteCursorImage.Source = _cursorImage;
                }
                catch
                {
                    // keep the previous shape
                }
            }
            _cursorVisible = c.Visible && _cursorImage is not null;
            _cursorX = c.X;
            _cursorY = c.Y;
            _cursorHotX = c.HotspotX;
            _cursorHotY = c.HotspotY;
            UpdateRemoteCursor();
        }
    }

    /// <summary>View-only: draw the remote pointer. Control mode: give the local mouse its shape (no network lag).</summary>
    private void UpdateRemoteCursor()
    {
        if (RemoteCursorImage is null || ScreenImage is null) return;

        if (_inputCall is not null)
        {
            RemoteCursorImage.IsVisible = false;
            ScreenImage.Cursor = !_cursorVisible ? new Cursor(StandardCursorType.None) : _cursorForControl;
            return;
        }

        ScreenImage.Cursor = null;
        var cw = ScreenImage.Bounds.Width;
        var ch = ScreenImage.Bounds.Height;
        if (!_cursorVisible || _cursorImage is null || _frameWidth <= 0 || _frameHeight <= 0 || cw <= 0 || ch <= 0)
        {
            RemoteCursorImage.IsVisible = false;
            return;
        }

        var scale = Math.Min(cw / _frameWidth, ch / _frameHeight);
        var offX = (cw - _frameWidth * scale) / 2;
        var offY = (ch - _frameHeight * scale) / 2;
        var inImage = new Point(offX + (_cursorX - _cursorHotX) * scale, offY + (_cursorY - _cursorHotY) * scale);
        var p = ScreenImage.TranslatePoint(inImage, CursorLayer) ?? inImage;

        RemoteCursorImage.Width = _cursorImage.PixelSize.Width * scale;
        RemoteCursorImage.Height = _cursorImage.PixelSize.Height * scale;
        Canvas.SetLeft(RemoteCursorImage, p.X);
        Canvas.SetTop(RemoteCursorImage, p.Y);
        RemoteCursorImage.IsVisible = true;
    }

    private void ResetRemoteCursor()
    {
        _cursorVisible = false;
        _cursorShapeId = -1;
        if (RemoteCursorImage is not null)
        {
            RemoteCursorImage.IsVisible = false;
            RemoteCursorImage.Source = null;
        }
        if (ScreenImage is not null) ScreenImage.Cursor = null;
    }

    /// <summary>The app's violet → cyan accent gradient (App.axaml "AccentGradient").</summary>
    private static Avalonia.Media.IBrush AccentBrush() =>
        Application.Current is { } app && app.TryGetResource("AccentGradient", null, out var v) && v is Avalonia.Media.IBrush b
            ? b
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(139, 92, 246));

    private sealed class StreamStats
    {
        private readonly object _gate = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private int _updates;
        private long _bytes;
        public int Width, Height;

        public void Add(int bytes, bool visual, int width, int height)
        {
            lock (_gate)
            {
                _bytes += bytes;
                if (visual) _updates++;
                Width = width;
                Height = height;
            }
        }

        public (double Fps, double MBps) TakeSecond()
        {
            lock (_gate)
            {
                var s = Math.Max(0.001, _sw.Elapsed.TotalSeconds);
                var result = (_updates / s, _bytes / s / (1024 * 1024));
                _updates = 0;
                _bytes = 0;
                _sw.Restart();
                return result;
            }
        }
    }

    private void UpdateStreamStatus(StreamStats stats)
    {
        if (_streamCts is null || stats.Width == 0) return;
        var (fps, mbps) = stats.TakeSecond();
        var rate = fps < 0.5 ? "idle" : $"{fps:0} fps";
        var line = $"LIVE  •  {rate}  •  {stats.Width}×{stats.Height}  •  {mbps:0.0} MB/s";
        StreamStatus.Text = line;
        FullscreenHudText.Text = $"{_agentInfo?.MachineName}  •  {line}";
    }
}
