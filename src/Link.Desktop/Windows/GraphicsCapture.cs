using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Link.Desktop.Windows;

/// <summary>
/// One frame of a window or a monitor through Windows.Graphics.Capture: what the compositor draws for it. A game, a
/// video or a page drawn by the GPU comes out as seen, which a copy of the screen often misses, and a window comes out
/// whole even under Link's overlay. Nothing stays open between frames.
/// </summary>
internal static class GraphicsCapture
{
    private static readonly Guid GraphicsCaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceId = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception exception) when (exception is COMException or TypeLoadException)
            {
                return false;
            }
        }
    }

    public static Task<Bitmap> CaptureWindowAsync(IntPtr window, TimeSpan timeout) =>
        CaptureAsync(interop => interop.CreateForWindow(window, GraphicsCaptureItemId), timeout);

    public static Task<Bitmap> CaptureMonitorAsync(IntPtr monitor, TimeSpan timeout) =>
        CaptureAsync(interop => interop.CreateForMonitor(monitor, GraphicsCaptureItemId), timeout);

    private static async Task<Bitmap> CaptureAsync(Func<IGraphicsCaptureItemInterop, IntPtr> create, TimeSpan timeout)
    {
        var item = CreateItem(create);
        if (item.Size.Width <= 0 || item.Size.Height <= 0) throw new InvalidOperationException("There is nothing to capture.");
        using var device = CreateDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        // Windows draws a yellow frame around what is captured unless told otherwise, where it can be.
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            session.IsBorderRequired = false;
        }

        var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            var frame = sender.TryGetNextFrame();
            if (frame is not null && !arrived.TrySetResult(frame)) frame.Dispose();
        };
        session.StartCapture();
        using var frame = await arrived.Task.WaitAsync(timeout).ConfigureAwait(false);
        using var copy = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
        // The pool's surface keeps the size the window had when capture began; the frame says how much is drawn.
        var width = Math.Min(frame.ContentSize.Width, copy.PixelWidth);
        var height = Math.Min(frame.ContentSize.Height, copy.PixelHeight);
        return ToBitmap(copy, width, height);
    }

    private static GraphicsCaptureItem CreateItem(Func<IGraphicsCaptureItemInterop, IntPtr> create)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var pointer = create(interop);
        try
        {
            return GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>A Direct3D device for the capture: the GPU's, or Windows' software one where there is none (RDP).</summary>
    private static IDirect3DDevice CreateDevice()
    {
        var device = IntPtr.Zero;
        var context = IntPtr.Zero;
        var dxgi = IntPtr.Zero;
        var inspectable = IntPtr.Zero;
        try
        {
            var result = NativeMethods.D3D11CreateDevice(IntPtr.Zero, NativeMethods.D3DDriverTypeHardware, IntPtr.Zero,
                NativeMethods.D3D11CreateDeviceBgraSupport, IntPtr.Zero, 0, NativeMethods.D3D11SdkVersion, out device, out _, out context);
            if (result < 0)
            {
                result = NativeMethods.D3D11CreateDevice(IntPtr.Zero, NativeMethods.D3DDriverTypeWarp, IntPtr.Zero,
                    NativeMethods.D3D11CreateDeviceBgraSupport, IntPtr.Zero, 0, NativeMethods.D3D11SdkVersion, out device, out _, out context);
            }

            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in DxgiDeviceId, out dxgi));
            Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            foreach (var pointer in new[] { inspectable, dxgi, context, device })
            {
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            }
        }
    }

    private static Bitmap ToBitmap(SoftwareBitmap source, int width, int height)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyToBuffer(pixels.AsBuffer());
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(pixels, row * stride, data.Scan0 + row * data.Stride, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    [ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] in Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] in Guid iid);
    }
}
