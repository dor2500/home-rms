using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using RawRect = Vortice.RawRect;

namespace HomeRms.Agent.Features;

/// <summary>
/// GPU screen capture via DXGI Desktop Duplication. Windows hands us the composed desktop
/// straight from the GPU plus the list of rectangles that changed since the previous frame,
/// so a 4K capture costs a few milliseconds instead of a full GDI BitBlt.
/// Keeps a CPU-side BGRA copy of the primary monitor that is updated only where it changed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopDuplicator : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private ID3D11Texture2D? _staging;
    private bool _haveFirstImage;

    private RawRect[] _dirty = new RawRect[64];
    private OutduplMoveRect[] _moves = new OutduplMoveRect[16];
    private static readonly int RawRectSize = Marshal.SizeOf<RawRect>();
    private static readonly int MoveRectSize = Marshal.SizeOf<OutduplMoveRect>();

    /// <summary>Monitor size in physical pixels.</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>Top-left of this monitor in virtual-desktop coordinates (for the pointer position).</summary>
    public int OffsetX { get; }
    public int OffsetY { get; }

    /// <summary>Current desktop image, 32-bit BGRA, stride = Width * 4.</summary>
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    private DesktopDuplicator(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication,
        int width, int height, int offsetX, int offsetY)
    {
        _device = device;
        _context = context;
        _duplication = duplication;
        Width = width;
        Height = height;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Pixels = new byte[width * height * 4];
    }

    /// <summary>Opens duplication of the primary monitor, or returns null (e.g. RDP session, secure desktop).</summary>
    public static DesktopDuplicator? TryCreate(out string? error)
    {
        error = null;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            var desc = output.Description;
                            if (!desc.AttachedToDesktop || desc.DesktopCoordinates.Left != 0 || desc.DesktopCoordinates.Top != 0)
                                continue; // the primary monitor is the one at (0,0)

                            var hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                                out ID3D11Device device, out ID3D11DeviceContext context);
                            if (hr.Failure || device is null || context is null)
                            {
                                error = $"D3D11CreateDevice failed: {hr}";
                                return null;
                            }

                            IDXGIOutputDuplication duplication;
                            try
                            {
                                // DuplicateOutput1 needs a per-monitor DPI aware process; fall back to the older API
                                IDXGIOutputDuplication? dup1 = null;
                                using (var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>())
                                {
                                    if (output5 is not null)
                                    {
                                        try { dup1 = output5.DuplicateOutput1(device, new[] { Format.B8G8R8A8_UNorm }); }
                                        catch { dup1 = null; }
                                    }
                                }
                                if (dup1 is null)
                                {
                                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                                    dup1 = output1.DuplicateOutput(device);
                                }
                                duplication = dup1;
                            }
                            catch (Exception ex)
                            {
                                context.Dispose();
                                device.Dispose();
                                error = "DuplicateOutput failed: " + ex.Message;
                                return null;
                            }

                            var dd = duplication.Description;
                            if (dd.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                            {
                                duplication.Dispose();
                                context.Dispose();
                                device.Dispose();
                                error = "Rotated displays are captured with GDI.";
                                return null;
                            }

                            return new DesktopDuplicator(device, context, duplication,
                                (int)dd.ModeDescription.Width, (int)dd.ModeDescription.Height,
                                desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top);
                        }
                    }
                }
            }

            error = "No primary display output found.";
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a new desktop image. Returns true and fills
    /// <paramref name="changed"/> when pixels changed; false on timeout or pointer-only updates.
    /// Throws when access is lost (desktop switch, resolution change) — dispose and recreate.
    /// </summary>
    public bool TryAcquire(int timeoutMs, List<Rectangle> changed)
    {
        changed.Clear();
        var hr = _duplication.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out var resource);
        if (hr.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
            return false;
        hr.CheckError();

        try
        {
            if (info.LastPresentTime == 0 && _haveFirstImage)
                return false; // only the pointer moved

            if (!_haveFirstImage)
            {
                changed.Add(new Rectangle(0, 0, Width, Height));
            }
            else
            {
                CollectChangedRects(info.TotalMetadataBufferSize, changed);
                if (changed.Count == 0)
                    return false;
            }

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            _staging ??= _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
            _context.CopyResource(_staging, texture);

            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyRects(map.DataPointer, (int)map.RowPitch, changed);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            _haveFirstImage = true;
            return true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    private void CollectChangedRects(uint metadataSize, List<Rectangle> changed)
    {
        if (metadataSize == 0) return;

        // Moved regions: the destination now holds new pixels
        var needed = (int)metadataSize / MoveRectSize + 1;
        if (_moves.Length < needed) _moves = new OutduplMoveRect[needed];
        if (_duplication.GetFrameMoveRects((uint)(_moves.Length * MoveRectSize), _moves, out var moveBytes).Success)
        {
            for (var i = 0; i < moveBytes / MoveRectSize; i++)
                AddClamped(changed, _moves[i].DestinationRect);
        }

        needed = (int)metadataSize / RawRectSize + 1;
        if (_dirty.Length < needed) _dirty = new RawRect[needed];
        if (_duplication.GetFrameDirtyRects((uint)(_dirty.Length * RawRectSize), _dirty, out var dirtyBytes).Success)
        {
            for (var i = 0; i < dirtyBytes / RawRectSize; i++)
                AddClamped(changed, _dirty[i]);
        }
    }

    private void AddClamped(List<Rectangle> list, RawRect r)
    {
        var left = Math.Clamp(r.Left, 0, Width);
        var top = Math.Clamp(r.Top, 0, Height);
        var right = Math.Clamp(r.Right, 0, Width);
        var bottom = Math.Clamp(r.Bottom, 0, Height);
        if (right > left && bottom > top)
            list.Add(Rectangle.FromLTRB(left, top, right, bottom));
    }

    private unsafe void CopyRects(IntPtr src, int srcPitch, List<Rectangle> rects)
    {
        var dstStride = Stride;
        fixed (byte* dstBase = Pixels)
        {
            var srcBase = (byte*)src;
            foreach (var r in rects)
            {
                var bytes = r.Width * 4;
                for (var y = r.Top; y < r.Bottom; y++)
                {
                    Buffer.MemoryCopy(srcBase + (long)y * srcPitch + r.Left * 4,
                        dstBase + (long)y * dstStride + r.Left * 4, bytes, bytes);
                }
            }
        }
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
