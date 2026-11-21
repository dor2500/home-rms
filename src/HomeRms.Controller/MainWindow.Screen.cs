using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
/// Decoding happens on the stream worker; the UI thread drains everything that arrived since its last
/// paint in one go (latest frame wins: updates older than the newest full frame are skipped).
/// </summary>
public partial class MainWindow
{
    private WriteableBitmap? _canvas;
    private long _lastLatencyMs = -1;

    // Decoded updates waiting for the UI thread (worker -> UI hand-off)
    private readonly object _paintGate = new();
    private List<ScreenUpdate> _pendingPaint = new();
    private List<ScreenUpdate> _paintBatch = new();
    private bool _paintScheduled;
    private int _streamGeneration;

    // Remote pointer
    private long _cursorShapeId = -1;
    private BitmapSource? _cursorImage;
    private Cursor? _cursorForControl;
    private bool _cursorVisible;
    private int _cursorX, _cursorY, _cursorHotX, _cursorHotY;

    /// <summary>One decoded update, ready to paint on the UI thread.</summary>
    private sealed class ScreenUpdate
    {
        public int Generation;
        public int Width;
        public int Height;
        public long CaptureUnixMs;
        public readonly List<Region> Regions = new();
        public ScreenCursor? Cursor;
        public BitmapSource? CursorImage;
        public Cursor? CursorForControl;

        /// <summary>True when the update repaints the whole canvas (earlier updates can be skipped).</summary>
        public bool IsFull => Regions.Count > 0 && Regions[0] is { X: 0, Y: 0 } r0 && r0.W >= Width && r0.H >= Height;

        /// <summary>Pixels come from <see cref="ArrayPool{T}.Shared"/>; <see cref="Release"/> returns them.</summary>
        public sealed record Region(int X, int Y, int W, int H, byte[] Pixels, int Stride);

        public void Release()
        {
            foreach (var r in Regions) ArrayPool<byte>.Shared.Return(r.Pixels);
            Regions.Clear();
        }

        public static ScreenUpdate Decode(ScreenFrame frame, int generation)
        {
            var u = new ScreenUpdate { Generation = generation, Width = frame.Width, Height = frame.Height, Cursor = frame.Cursor, CaptureUnixMs = frame.CaptureUnixMs };

            if (!frame.Jpeg.IsEmpty)
            {
                var full = DecodeRegion(frame.Jpeg, png: false, 0, 0);
                u.Regions.Add(full);
                // Legacy/scaled frames define the canvas size by themselves
                if (frame.Tiles.Count == 0) { u.Width = full.W; u.Height = full.H; }
            }

            if (frame.Tiles.Count == 1)
            {
                var t = frame.Tiles[0];
                u.Regions.Add(DecodeRegion(t.Jpeg, t.Format == 1, t.X, t.Y));
            }
            else if (frame.Tiles.Count > 1)
            {
                var decoded = new Region[frame.Tiles.Count];
                Parallel.For(0, frame.Tiles.Count, i =>
                {
                    var t = frame.Tiles[i];
                    decoded[i] = DecodeRegion(t.Jpeg, t.Format == 1, t.X, t.Y);
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

        /// <summary>Decodes one JPEG (format 0) or lossless PNG (format 1) image into pooled Bgr32 pixels.</summary>
        private static Region DecodeRegion(Google.Protobuf.ByteString data, bool png, int x, int y)
        {
            // Read straight from the protobuf buffer (no extra copy of the compressed bytes)
            var stream = MemoryMarshal.TryGetArray(data.Memory, out var seg) && seg.Array is not null
                ? new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false)
                : new MemoryStream(data.ToByteArray());
            BitmapDecoder decoder = png
                ? new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
                : new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource src = decoder.Frames[0];
            if (src.Format != PixelFormats.Bgr32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            var w = src.PixelWidth;
            var h = src.PixelHeight;
            var stride = w * 4;
            var pixels = ArrayPool<byte>.Shared.Rent(stride * h);
            src.CopyPixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
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

    /// <summary>Worker side: hands a decoded update to the UI thread (one paint pass per UI turn).</summary>
    private void QueuePaint(ScreenUpdate u)
    {
        bool schedule;
        lock (_paintGate)
        {
            _pendingPaint.Add(u);
            schedule = !_paintScheduled;
            _paintScheduled = true;
        }
        if (schedule)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, PaintPending);
    }

    /// <summary>Updates decoded but not painted yet (the worker slows its network reads above a few).</summary>
    private int PendingPaintCount
    {
        get { lock (_paintGate) return _pendingPaint.Count; }
    }

    /// <summary>UI side: paints everything that arrived since the last pass, skipping what a newer full frame covers.</summary>
    private void PaintPending()
    {
        List<ScreenUpdate> batch;
        lock (_paintGate)
        {
            batch = _pendingPaint;
            _pendingPaint = _paintBatch;
            _paintBatch = batch;
            _paintScheduled = false;
        }

        try
        {
            // Drop updates of a stream that was stopped / restarted (preset or monitor switch)
            batch.RemoveAll(u =>
            {
                if (u.Generation == _streamGeneration) return false;
                u.Release();
                return true;
            });
            if (batch.Count == 0) return;

            // Latest frame wins: nothing before the newest full frame will be visible anyway
            var first = 0;
            for (var i = batch.Count - 1; i > 0; i--)
                if (batch[i].IsFull) { first = i; break; }

            ScreenCursor? cursor = null;
            for (var i = 0; i < batch.Count; i++)
            {
                var u = batch[i];
                if (u.CursorImage is not null) ApplyCursorShape(u); // shapes are sent once: never skip them
                if (i >= first) PaintRegions(u);
                if (u.Cursor is not null) cursor = u.Cursor;
                u.Release();
            }
            if (cursor is not null) ApplyCursorPosition(cursor);
        }
        finally
        {
            batch.Clear();
        }
    }

    private void PaintRegions(ScreenUpdate u)
    {
        if (u.Width <= 0 || u.Height <= 0 || u.Regions.Count == 0) return;

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
        if (DesktopOverlay.Visibility != Visibility.Collapsed) DesktopOverlay.Visibility = Visibility.Collapsed;

        if (u.CaptureUnixMs > 0)
            _lastLatencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - u.CaptureUnixMs;

        // Copy rows straight into the back buffer: one lock for all regions of the update
        _canvas.Lock();
        try
        {
            var back = _canvas.BackBuffer;
            var backStride = _canvas.BackBufferStride;
            foreach (var r in u.Regions)
            {
                var w = Math.Min(r.W, u.Width - r.X);
                var h = Math.Min(r.H, u.Height - r.Y);
                if (w <= 0 || h <= 0 || r.X < 0 || r.Y < 0) continue;
                var rowBytes = w * 4;
                for (var y = 0; y < h; y++)
                    Marshal.Copy(r.Pixels, y * r.Stride, back + (r.Y + y) * backStride + r.X * 4, rowBytes);
                _canvas.AddDirtyRect(new Int32Rect(r.X, r.Y, w, h));
            }
        }
        finally
        {
            _canvas.Unlock();
        }
    }

    private void ApplyCursorShape(ScreenUpdate u)
    {
        if (u.Cursor is not { } c || u.CursorImage is null || c.ShapeId == _cursorShapeId) return;
        _cursorShapeId = c.ShapeId;
        _cursorImage = u.CursorImage;
        _cursorForControl = u.CursorForControl;
        RemoteCursorImage.Source = _cursorImage;
    }

    private void ApplyCursorPosition(ScreenCursor c)
    {
        _cursorVisible = c.Visible && _cursorImage is not null;
        _cursorX = c.X;
        _cursorY = c.Y;
        _cursorHotX = c.HotspotX;
        _cursorHotY = c.HotspotY;
        UpdateRemoteCursor();
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
            // Never hide the local cursor (Cursors.None) when remote GetCursorInfo reports hidden
            // (e.g. while typing, on headless PCs, or before the first shape arrives); fall back to the local arrow.
            ScreenImage.Cursor = _cursorVisible && _cursorForControl is not null ? _cursorForControl : null;
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

    private void OnRemoteScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (ScreenImage?.Source is not BitmapSource bmp)
        {
            StatusText.Text = "No remote screen frame is currently displayed.";
            return;
        }
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "HomeRMS");
            System.IO.Directory.CreateDirectory(dir);
            var target = (_connectedProfile?.DisplayTitle ?? _agentInfo?.MachineName ?? "remote")
                .Replace(' ', '_');
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                target = target.Replace(c, '_');
            var file = System.IO.Path.Combine(dir, $"screenshot-{target}-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(file))
                enc.Save(fs);

            try { Clipboard.SetImage(bmp); } catch { }
            StatusText.Text = $"Saved screenshot to {file} (also copied to clipboard).";
            AddActivity("SCREEN", $"Saved remote screenshot: {file}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Screenshot failed: {ex.Message}";
        }
    }

    /// <summary>Paints the Windows 11 title bar in the app's own colours instead of the system accent.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
            int caption = Themes.Palette.ColorRef("SurfaceLayerBrush"); // title bar matches the header surface
            int text = Themes.Palette.ColorRef("TextPrimaryBrush");
            int border = Themes.Palette.ColorRef("BorderSubtleBrush");
            DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
        }
        catch
        {
            // Older Windows: keep the default title bar
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
            // Exactly 1:1: nearest neighbour keeps text pixel-crisp; any real resampling gets high quality
            var oneToOne = Math.Abs(scale - 1) < 0.01;
            RenderOptions.SetBitmapScalingMode(ScreenImage, oneToOne ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
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

    /// <summary>Per-second counters for the status pill (and the loopback measurements).</summary>
    private sealed class StreamStats
    {
        private readonly object _gate = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private int _updates;
        private long _bytes;
        public int Width, Height;
        public string PresetLabel = "";

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
        // Capture -> paint; only meaningful when both clocks agree, so hide obviously skewed values
        var latency = _lastLatencyMs is >= 0 and < 5000 ? $"  •  {_lastLatencyMs} ms" : "";
        var line = $"LIVE  •  {stats.PresetLabel}  •  {rate}  •  {stats.Width}×{stats.Height}  •  {mbps:0.0} MB/s{latency}";
        StreamStatus.Text = line;
        FullscreenHudText.Text = $"{_agentInfo?.MachineName}  •  {line}";
        StreamStatus.ToolTip = _lastLatencyMs >= 0
            ? $"Capture → screen ≈ {_lastLatencyMs} ms (accurate only if both PCs' clocks are in sync)"
            : null;
    }
}
