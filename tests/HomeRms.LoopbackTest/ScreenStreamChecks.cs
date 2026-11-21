using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Grpc.Core;
using HomeRms.Agent.Features;
using HomeRms.Shared.Protocol;

/// <summary>
/// Screen streaming with the LAN quality options: encoder unit checks (PNG round trip is pixel-exact,
/// 4:2:0 is smaller than 4:4:4), the new request fields over a real stream (60 fps, 4:4:4, lossless refine
/// -> PNG tiles once the screen is still), and a short LAN High Quality vs Balanced measurement against a
/// small animated window.
/// </summary>
internal static class ScreenStreamChecks
{
    public static void RunEncoderUnitChecks(List<string> failures)
    {
        var before = failures.Count;
        void Check(bool ok, string what) { if (!ok) failures.Add("screen encoder check: " + what); }

        // Synthetic "screen": text-like noise, hard colour edges and a gradient (odd size on purpose)
        const int w = 203, h = 117;
        var stride = (w * 3 + 3) & ~3;
        var bgr = new byte[stride * h];
        var rng = new Random(1234);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = y * stride + x * 3;
            if (x < 60) { bgr[o] = (byte)rng.Next(256); bgr[o + 1] = (byte)rng.Next(256); bgr[o + 2] = (byte)rng.Next(256); }
            else if (x < 130) { var red = (x / 7 + y / 5) % 2 == 0; bgr[o] = red ? (byte)0 : (byte)255; bgr[o + 1] = 0; bgr[o + 2] = red ? (byte)255 : (byte)0; }
            else { bgr[o] = (byte)(x * 3); bgr[o + 1] = (byte)(y * 2); bgr[o + 2] = (byte)(x + y); }
        }

        var enc = new WicJpegEncoder();
        var png = enc.EncodePng(bgr, w, h, stride);
        Check(png.Length > 8 && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G', "EncodePng writes a PNG signature");

        using (var decoded = new Bitmap(new MemoryStream(png)))
        {
            Check(decoded.Width == w && decoded.Height == h, $"PNG keeps the size ({decoded.Width}x{decoded.Height})");
            var data = decoded.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var back = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, back, 0, back.Length);
                var mismatches = 0;
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w * 3; x++)
                    if (back[y * data.Stride + x] != bgr[y * stride + x]) mismatches++;
                Check(mismatches == 0, $"PNG round trip is pixel-exact ({mismatches} differing bytes)");
            }
            finally { decoded.UnlockBits(data); }
        }

        var j444 = enc.Encode(bgr, w, h, stride, 90, fullChroma: true);
        var j420 = enc.Encode(bgr, w, h, stride, 90, fullChroma: false);
        Check(j444.Length > 100 && j420.Length > 100 && j420.Length < j444.Length, $"4:2:0 JPEG ({j420.Length} B) is smaller than 4:4:4 ({j444.Length} B)");

        Console.WriteLine($"[test] Screen encoder unit checks -> {(failures.Count == before ? "all passed" : $"{failures.Count - before} failed")} " +
                          $"(PNG {png.Length} B, JPEG 4:4:4 {j444.Length} B, 4:2:0 {j420.Length} B)");
    }

    /// <summary>Requires an active consent grant (auto-approve UI attached).</summary>
    public static async Task RunStreamChecksAsync(RemoteManagement.RemoteManagementClient client, List<string> failures)
    {
        // 1) New fields: frames arrive; once the desktop is still for > refine delay, PNG tiles (format 1) appear
        var lanRequest = new ScreenRequest
        {
            Fps = 60, Quality = 95, OperatorName = "loopback-test", SupportsTiles = true,
            Chroma = 2, LosslessRefine = true, RefineDelayMs = 120
        };
        var still = await StreamAsync(client, lanRequest, TimeSpan.FromSeconds(4), stopAtFirstPng: true);
        Console.WriteLine($"[test] StreamScreen (60 fps, 4:4:4, lossless refine) -> {still.Frames} frames, {still.PngTiles} PNG tiles, tile path={still.TilePath}" +
                          (still.FirstPngAfterMs >= 0 ? $", first PNG tile after {still.FirstPngAfterMs} ms" : ""));
        if (still.Frames == 0) failures.Add("StreamScreen with the LAN fields delivered no frame");
        if (still.BadPng > 0) failures.Add($"{still.BadPng} PNG tiles did not decode to their declared size");
        if (still.TilePath && still.PngTiles == 0)
            failures.Add("lossless refine: no PNG tile (format 1) within 4 s although GPU tile streaming was active");
        if (!still.TilePath)
            Console.WriteLine("[test]   (GPU capture unavailable here: GDI frames only, the PNG tile check is covered by the encoder unit check)");

        // 2) Measurement: an animated window, LAN High Quality vs Balanced
        using var motion = MotionWindow.Show();
        await Task.Delay(500);
        var presets = new (string Name, ScreenRequest Request)[]
        {
            ("LAN HQ", lanRequest),
            ("Balanced", new ScreenRequest { Fps = 30, Quality = 85, OperatorName = "loopback-test", SupportsTiles = true, Chroma = 2 }),
        };
        foreach (var (name, request) in presets)
        {
            var paintsBefore = motion.Paints;
            var r = await StreamAsync(client, request, TimeSpan.FromSeconds(4), stopAtFirstPng: false);
            var sourceFps = (motion.Paints - paintsBefore) / r.Elapsed.TotalSeconds;
            Console.WriteLine($"[measure] {name,-8} req {request.Fps} fps (window repaints {sourceFps:0} /s): {r.Frames / r.Elapsed.TotalSeconds:0.0} fps achieved, " +
                              $"{r.Bytes / r.Elapsed.TotalSeconds / (1024 * 1024):0.00} MB/s, avg frame {Kb(r.Bytes, r.Frames)}, " +
                              $"keyframes {r.KeyFrames} (avg {Kb(r.KeyBytes, r.KeyFrames)}), JPEG tiles {r.JpegTiles} (avg {Kb(r.JpegBytes, r.JpegTiles)}), " +
                              $"PNG tiles {r.PngTiles} (avg {Kb(r.PngBytes, r.PngTiles)}), frame gap p50 {r.GapP50:0.0} ms");
            if (r.Frames == 0) failures.Add($"{name} measurement stream delivered no frame");
        }
    }

    private static string Kb(long bytes, int count) => count == 0 ? "-" : $"{bytes / (double)count / 1024:0.0} KB";

    private sealed class StreamResult
    {
        public int Frames, KeyFrames, JpegTiles, PngTiles, BadPng;
        public long Bytes, KeyBytes, JpegBytes, PngBytes, FirstPngAfterMs = -1;
        public bool TilePath;
        public TimeSpan Elapsed;
        public double GapP50;
    }

    private static async Task<StreamResult> StreamAsync(RemoteManagement.RemoteManagementClient client, ScreenRequest request, TimeSpan duration, bool stopAtFirstPng)
    {
        var r = new StreamResult();
        var gaps = new List<double>();
        using var cts = new CancellationTokenSource(duration);
        var sw = Stopwatch.StartNew();
        double last = -1;
        try
        {
            using var call = client.StreamScreen(request, cancellationToken: cts.Token);
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
                var f = call.ResponseStream.Current;
                var now = sw.Elapsed.TotalMilliseconds;
                if (last >= 0) gaps.Add(now - last);
                last = now;
                r.Frames++;
                r.Bytes += f.CalculateSize();
                if (f.Cursor is not null) r.TilePath = true;
                if (!f.Jpeg.IsEmpty) { r.KeyFrames++; r.KeyBytes += f.Jpeg.Length; }
                foreach (var t in f.Tiles)
                {
                    if (t.Format == 1)
                    {
                        r.PngTiles++;
                        r.PngBytes += t.Jpeg.Length;
                        if (r.FirstPngAfterMs < 0) r.FirstPngAfterMs = (long)now;
                        try
                        {
                            using var bmp = new Bitmap(new MemoryStream(t.Jpeg.ToByteArray()));
                            if (bmp.Width != t.Width || bmp.Height != t.Height) r.BadPng++;
                        }
                        catch { r.BadPng++; }
                    }
                    else
                    {
                        r.JpegTiles++;
                        r.JpegBytes += t.Jpeg.Length;
                    }
                }
                if (stopAtFirstPng && r.PngTiles > 0) break;
            }
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded) { }
        catch (OperationCanceledException) { }
        r.Elapsed = sw.Elapsed;
        gaps.Sort();
        r.GapP50 = gaps.Count == 0 ? 0 : gaps[gaps.Count / 2];
        return r;
    }

    /// <summary>A small always-on-top window that redraws a moving pattern ~60 times a second.</summary>
    private sealed class MotionWindow : IDisposable
    {
        private readonly Thread _thread;
        private MotionForm? _form;

        public int Paints => _form?.Paints ?? 0;

        private MotionWindow()
        {
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                var form = new MotionForm();
                _form = form;
                form.Shown += (_, _) => ready.Set();
                Application.Run(form);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        public static MotionWindow Show() => new();

        public void Dispose()
        {
            try { _form?.BeginInvoke(() => _form.Close()); } catch { }
            _thread.Join(TimeSpan.FromSeconds(3));
        }

        private sealed class MotionForm : Form
        {
            private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1 };
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            public volatile int Paints;

            public MotionForm()
            {
                Text = "Home RMS loopback - motion";
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(80, 80, 640, 360);
                TopMost = true;
                ShowInTaskbar = false;
                DoubleBuffered = true;
                _timer.Tick += (_, _) => Invalidate();
                _timer.Start();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Paints++;
                var t = _sw.Elapsed.TotalSeconds;
                var g = e.Graphics;
                g.Clear(Color.FromArgb(30, 30, 36));
                var x = (int)((Math.Sin(t * 2.1) + 1) / 2 * (ClientSize.Width - 120));
                using var brush = new SolidBrush(Color.FromArgb(255, (int)(t * 90) % 256, 140, 220));
                g.FillRectangle(brush, x, 40, 120, 120);
                using var font = new Font("Segoe UI", 14);
                g.DrawString($"frame time {t:0.000} s  •  colour text sample", font, Brushes.Gold, 10, 200);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _timer.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
