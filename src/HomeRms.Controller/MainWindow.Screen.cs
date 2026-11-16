using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HomeRms.Shared.Protocol;

namespace HomeRms.Controller;

/// <summary>
/// Remote-screen rendering: keeps one WriteableBitmap the size of the remote desktop and paints
/// full frames or dirty tiles into it, and draws the remote pointer separately.
/// </summary>
public partial class MainWindow
{
    private WriteableBitmap? _canvas;
    private int _uiQueued;
    private long _lastLatencyMs = -1;

    // Remote pointer
    private long _cursorShapeId = -1;
    private BitmapSource? _cursorImage;
    private Cursor? _cursorForControl;
    private bool _cursorVisible;
    private int _cursorX, _cursorY, _cursorHotX, _cursorHotY;

    /// <summary>One decoded update, ready to paint on the UI thread.</summary>
    private sealed class ScreenUpdate
    {
        public int Width;
        public int Height;
        public long CaptureUnixMs;
        public readonly List<Region> Regions = new();
        public ScreenCursor? Cursor;
        public BitmapSource? CursorImage;
        public Cursor? CursorForControl;

        public sealed record Region(int X, int Y, int W, int H, byte[] Pixels, int Stride);

        public static ScreenUpdate Decode(ScreenFrame frame)
        {
            var u = new ScreenUpdate { Width = frame.Width, Height = frame.Height, Cursor = frame.Cursor, CaptureUnixMs = frame.CaptureUnixMs };

            if (!frame.Jpeg.IsEmpty)
            {
                var full = DecodeRegion(frame.Jpeg.ToByteArray(), 0, 0);
                u.Regions.Add(full);
                // Legacy/scaled frames define the canvas size by themselves
                if (frame.Tiles.Count == 0) { u.Width = full.W; u.Height = full.H; }
            }

            if (frame.Tiles.Count == 1)
            {
                var t = frame.Tiles[0];
                u.Regions.Add(DecodeRegion(t.Jpeg.ToByteArray(), t.X, t.Y));
            }
            else if (frame.Tiles.Count > 1)
            {
                var decoded = new Region[frame.Tiles.Count];
                Parallel.For(0, frame.Tiles.Count, i =>
                {
                    var t = frame.Tiles[i];
                    decoded[i] = DecodeRegion(t.Jpeg.ToByteArray(), t.X, t.Y);
                });
                u.Regions.AddRange(decoded);
            }

            if (frame.Cursor is { ShapePng.IsEmpty: false } c)
            {
                try
                {
                    var png = c.ShapePng.ToByteArray();
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = new MemoryStream(png);
                    img.EndInit();
                    img.Freeze();
                    u.CursorImage = img;
                    u.CursorForControl = BuildCursor(png, img.PixelWidth, img.PixelHeight, c.HotspotX, c.HotspotY);
                }
                catch
                {
                    // keep the previous pointer shape
                }
            }

            return u;
        }

        private static Region DecodeRegion(byte[] jpeg, int x, int y)
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource src = decoder.Frames[0];
            if (src.Format != PixelFormats.Bgr32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            var w = src.PixelWidth;
            var h = src.PixelHeight;
            var stride = w * 4;
            var pixels = new byte[stride * h];
            src.CopyPixels(pixels, stride, 0);
            return new Region(x, y, w, h, pixels, stride);
        }

        /// <summary>Wraps the pointer PNG in a .cur container so it can be the local mouse cursor.</summary>
        private static Cursor? BuildCursor(byte[] png, int w, int h, int hotX, int hotY)
        {
            try
            {
                var ms = new MemoryStream();
                using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
                {
                    bw.Write((short)0);            // reserved
                    bw.Write((short)2);            // type: cursor
                    bw.Write((short)1);            // image count
                    bw.Write((byte)(w >= 256 ? 0 : w));
                    bw.Write((byte)(h >= 256 ? 0 : h));
                    bw.Write((byte)0);             // palette
                    bw.Write((byte)0);             // reserved
                    bw.Write((short)hotX);
                    bw.Write((short)hotY);
                    bw.Write(png.Length);
                    bw.Write(22);                  // offset of image data
                    bw.Write(png);
                }
                ms.Position = 0;
                return new Cursor(ms);
            }
            catch
            {
                return null;
            }
        }
    }

    private void ApplyScreenUpdate(ScreenUpdate u)
    {
        if (u.Width <= 0 || u.Height <= 0) return;

        if (u.Regions.Count > 0)
        {
            if (_canvas is null || _canvas.PixelWidth != u.Width || _canvas.PixelHeight != u.Height)
            {
                _canvas = new WriteableBitmap(u.Width, u.Height, 96, 96, PixelFormats.Bgr32, null);
                ScreenImage.Source = _canvas;
                _frameWidth = u.Width;
                _frameHeight = u.Height;
                ApplyUpscaling();
            }

            _frameWidth = u.Width;
            _frameHeight = u.Height;
            DesktopOverlay.Visibility = Visibility.Collapsed;

            if (u.CaptureUnixMs > 0)
                _lastLatencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - u.CaptureUnixMs;

            foreach (var r in u.Regions)
            {
                var w = Math.Min(r.W, u.Width - r.X);
                var h = Math.Min(r.H, u.Height - r.Y);
                if (w <= 0 || h <= 0 || r.X < 0 || r.Y < 0) continue;
                _canvas.WritePixels(new Int32Rect(r.X, r.Y, w, h), r.Pixels, r.Stride, 0);
            }
        }

        if (u.Cursor is { } c)
        {
            if (u.CursorImage is not null && c.ShapeId != _cursorShapeId)
            {
                _cursorShapeId = c.ShapeId;
                _cursorImage = u.CursorImage;
                _cursorForControl = u.CursorForControl;
                RemoteCursorImage.Source = _cursorImage;
            }
            _cursorVisible = c.Visible && _cursorImage is not null;
            _cursorX = c.X;
            _cursorY = c.Y;
            _cursorHotX = c.HotspotX;
            _cursorHotY = c.HotspotY;
            UpdateRemoteCursor();
        }
    }

    /// <summary>
    /// View-only: draw the remote pointer on top of the picture.
    /// Control mode: hide it and give the local mouse the remote pointer's shape instead,
    /// so the pointer you see moves with zero network delay.
    /// </summary>
    private void UpdateRemoteCursor()
    {
        if (RemoteCursorImage is null) return;
        var controlling = _inputCall is not null;

        if (controlling)
        {
            RemoteCursorImage.Visibility = Visibility.Collapsed;
            ScreenImage.Cursor = !_cursorVisible ? Cursors.None : _cursorForControl;
            return;
        }

        ScreenImage.Cursor = null;
        if (!_cursorVisible || _cursorImage is null || _frameWidth <= 0 || _frameHeight <= 0 ||
            ScreenImage.ActualWidth <= 0 || ScreenImage.ActualHeight <= 0)
        {
            RemoteCursorImage.Visibility = Visibility.Collapsed;
            return;
        }

        var cw = ScreenImage.ActualWidth;
        var ch = ScreenImage.ActualHeight;
        var scale = Math.Min(cw / _frameWidth, ch / _frameHeight);
        var offX = (cw - _frameWidth * scale) / 2;
        var offY = (ch - _frameHeight * scale) / 2;
        var inImage = new Point(offX + (_cursorX - _cursorHotX) * scale, offY + (_cursorY - _cursorHotY) * scale);
        var p = ScreenImage.TranslatePoint(inImage, CursorLayer);

        RemoteCursorImage.Width = _cursorImage.PixelWidth * scale;
        RemoteCursorImage.Height = _cursorImage.PixelHeight * scale;
        Canvas.SetLeft(RemoteCursorImage, p.X);
        Canvas.SetTop(RemoteCursorImage, p.Y);
        RemoteCursorImage.Visibility = Visibility.Visible;
    }

    private void ResetRemoteCursor()
    {
        _cursorVisible = false;
        _cursorShapeId = -1;
        _cursorImage = null;
        _cursorForControl = null;
        if (RemoteCursorImage is not null)
        {
            RemoteCursorImage.Visibility = Visibility.Collapsed;
            RemoteCursorImage.Source = null;
        }
        if (ScreenImage is not null) ScreenImage.Cursor = null;
    }

    private void OnScreenImageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRemoteCursor();
        ApplyUpscaling();
    }

    // ---------------- GPU upscaling ----------------
    private Effects.UpscaleEffect? _upscaleEffect;
    private Effects.SharpenEffect? _sharpenEffect;

    private void OnUpscaleModeChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyUpscaling();
        if (HudUpscaleText is null || UpscaleBox is null) return;
        var label = UpscaleBox.SelectedIndex switch { 0 => "Sharp+", 1 => "Sharp", _ => "Off" };
        HudUpscaleText.Text = label;
        HudUpscaleBtn.ToolTip = $"Upscaling: {label.ToLowerInvariant()} (click to change)";
    }

    /// <summary>Full-screen bar: cycles sharp+ → sharp → off.</summary>
    private void OnHudUpscaleClick(object sender, RoutedEventArgs e)
    {
        UpscaleBox.SelectedIndex = (UpscaleBox.SelectedIndex + 1) % UpscaleBox.Items.Count;
        ScreenImage.Focus();
    }

    /// <summary>
    /// Chooses how the remote picture is scaled to the window:
    /// enlarging → nearest-neighbour draw + Catmull-Rom shader (+ adaptive sharpening);
    /// shrinking or 1:1 → normal high-quality scaling. Falls back to plain scaling when the
    /// GPU has no pixel shader 3.0 support.
    /// </summary>
    private void ApplyUpscaling()
    {
        if (ScreenImage is null || SharpenHost is null || UpscaleBox is null) return;

        var mode = UpscaleBox.SelectedItem is ComboBoxItem { Tag: string t } ? t : "cas";
        var gpuOk = RenderCapability.IsPixelShaderVersionSupported(3, 0);
        var dpi = VisualTreeHelper.GetDpi(ScreenImage);
        var outW = ScreenImage.ActualWidth * dpi.DpiScaleX;
        var scale = _canvas is null || _frameWidth <= 0 ? 1.0 : outW / _frameWidth;
        var enlarging = scale > 1.05;

        var useBicubic = gpuOk && enlarging && mode is "cas" or "bicubic";
        if (useBicubic)
        {
            _upscaleEffect ??= new Effects.UpscaleEffect();
            _upscaleEffect.SourceSize = new Point(_frameWidth, _frameHeight);
            RenderOptions.SetBitmapScalingMode(ScreenImage, BitmapScalingMode.NearestNeighbor);
            ScreenImage.Effect = _upscaleEffect;
        }
        else
        {
            RenderOptions.SetBitmapScalingMode(ScreenImage, BitmapScalingMode.HighQuality);
            ScreenImage.Effect = null;
        }

        // Sharpening helps whenever the picture is resampled (not at exactly 1:1)
        var useSharpen = gpuOk && mode == "cas" && _canvas is not null && Math.Abs(scale - 1) > 0.05;
        if (useSharpen)
        {
            _sharpenEffect ??= new Effects.SharpenEffect { Sharpness = enlarging ? 0.6 : 0.35 };
            _sharpenEffect.Sharpness = enlarging ? 0.6 : 0.35;
            SharpenHost.Effect = _sharpenEffect;
        }
        else
        {
            SharpenHost.Effect = null;
        }
    }

    /// <summary>Per-second counters for the status pill.</summary>
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
        StreamStatus.ToolTip = _lastLatencyMs >= 0
            ? $"Capture → screen ≈ {_lastLatencyMs} ms (accurate only if both PCs' clocks are in sync)"
            : null;
    }
}
