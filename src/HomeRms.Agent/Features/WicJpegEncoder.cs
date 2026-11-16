using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace HomeRms.Agent.Features;

/// <summary>
/// JPEG encoder built on the Windows Imaging Component (WIC).
/// Unlike GDI+ (which always uses 4:2:0 chroma subsampling), WIC lets us encode with full
/// 4:4:4 chroma, so coloured text and thin UI lines stay sharp in the remote-desktop stream.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WicJpegEncoder
{
    private static readonly Guid ClsidWicImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid ContainerFormatJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");
    private static readonly Guid PixelFormat24bppBgr = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");

    private const uint WICBitmapEncoderNoCache = 2;
    private const byte WICJpegYCrCbSubsampling444 = 3;
    private const byte WICJpegYCrCbSubsampling420 = 1;

    private readonly IWICImagingFactory _factory;

    public WicJpegEncoder()
    {
        var type = Type.GetTypeFromCLSID(ClsidWicImagingFactory, throwOnError: true)!;
        _factory = (IWICImagingFactory)Activator.CreateInstance(type)!;
    }

    /// <summary>Encodes a 24-bit BGR pixel buffer (GDI+ Format24bppRgb layout) as JPEG.</summary>
    /// <param name="quality">1–100.</param>
    /// <param name="fullChroma">true = 4:4:4 (sharp colour edges), false = 4:2:0 (smaller).</param>
    public byte[] Encode(byte[] bgr, int width, int height, int stride, int quality, bool fullChroma)
    {
        CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream);
        IWICBitmapEncoder? encoder = null;
        IWICBitmapFrameEncode? frame = null;
        IPropertyBag2? options = null;
        try
        {
            var fmt = ContainerFormatJpeg;
            _factory.CreateEncoder(ref fmt, IntPtr.Zero, out encoder);
            encoder.Initialize(stream, WICBitmapEncoderNoCache);
            encoder.CreateNewFrame(out frame, out options);

            WriteOption(options, "ImageQuality", (float)(Math.Clamp(quality, 1, 100) / 100.0));
            WriteOption(options, "JpegYCrCbSubsampling", fullChroma ? WICJpegYCrCbSubsampling444 : WICJpegYCrCbSubsampling420);

            frame.Initialize(options);
            frame.SetSize((uint)width, (uint)height);
            frame.SetResolution(96, 96);
            var pf = PixelFormat24bppBgr;
            frame.SetPixelFormat(ref pf);
            if (pf != PixelFormat24bppBgr)
                throw new InvalidOperationException("WIC JPEG encoder rejected 24bpp BGR input.");

            var handle = GCHandle.Alloc(bgr, GCHandleType.Pinned);
            try
            {
                frame.WritePixels((uint)height, (uint)stride, (uint)(stride * height), handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            frame.Commit();
            encoder.Commit();
            return ReadAll(stream);
        }
        finally
        {
            if (options is not null) Marshal.ReleaseComObject(options);
            if (frame is not null) Marshal.ReleaseComObject(frame);
            if (encoder is not null) Marshal.ReleaseComObject(encoder);
            Marshal.ReleaseComObject(stream);
        }
    }

    private static void WriteOption(IPropertyBag2 bag, string name, object value)
    {
        var namePtr = Marshal.StringToCoTaskMemUni(name);
        try
        {
            var prop = new PROPBAG2 { pstrName = namePtr };
            bag.Write(1, ref prop, ref value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    private static byte[] ReadAll(IStream stream)
    {
        var posPtr = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            stream.Seek(0, 1 /* STREAM_SEEK_CUR */, posPtr);
            var length = Marshal.ReadInt64(posPtr);
            GetHGlobalFromStream(stream, out var hGlobal);
            var data = GlobalLock(hGlobal);
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return bytes;
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(posPtr);
        }
    }

    // ---------------- Interop ----------------

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CreateStreamOnHGlobal(IntPtr hGlobal, bool deleteOnRelease, out IStream stream);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void GetHGlobalFromStream(IStream stream, out IntPtr hGlobal);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPBAG2
    {
        public uint dwType;
        public ushort vt;
        public ushort cfType;
        public uint dwHint;
        public IntPtr pstrName;
        public Guid clsid;
    }

    [ComImport, Guid("22F55882-280B-11d0-A8A9-00A0C90C2004"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag2
    {
        void Read(); // not used (vtable slot)
        void Write(uint cProperties, ref PROPBAG2 pPropBag, [MarshalAs(UnmanagedType.Struct)] ref object pvarValue);
    }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename();
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder(ref Guid guidContainerFormat, IntPtr pguidVendor, out IWICBitmapEncoder ppIEncoder);
    }

    [ComImport, Guid("00000103-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapEncoder
    {
        void Initialize(IStream pIStream, uint cacheOption);
        void GetContainerFormat();
        void GetEncoderInfo();
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void SetPreview();
        void CreateNewFrame(out IWICBitmapFrameEncode ppIFrameEncode, out IPropertyBag2 ppIEncoderOptions);
        void Commit();
    }

    [ComImport, Guid("00000105-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameEncode
    {
        void Initialize(IPropertyBag2 pIEncoderOptions);
        void SetSize(uint uiWidth, uint uiHeight);
        void SetResolution(double dpiX, double dpiY);
        void SetPixelFormat(ref Guid pPixelFormat);
        void SetColorContexts();
        void SetPalette();
        void SetThumbnail();
        void WritePixels(uint lineCount, uint cbStride, uint cbBufferSize, IntPtr pbPixels);
        void WriteSource();
        void Commit();
    }
}
