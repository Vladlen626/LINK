using System.Drawing;
using System.Drawing.Imaging;
using Link.Desktop.Services;

namespace Link.Desktop.Windows;

public sealed class ScreenCaptureService
{
    private readonly ScreenshotHistoryService _history;

    public ScreenCaptureService(ScreenshotHistoryService history) => _history = history;

    public ScreenshotResult CaptureCurrentMonitor()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var screen = System.Windows.Forms.Screen.FromPoint(new Point(cursor.X, cursor.Y));
        return CaptureRectangle(screen.Bounds, "monitor");
    }

    public ScreenshotResult CaptureDesktop() =>
        CaptureRectangle(System.Windows.Forms.SystemInformation.VirtualScreen, "desktop");

    public IReadOnlyList<ScreenshotResult> CaptureAllMonitors()
    {
        var results = System.Windows.Forms.Screen.AllScreens
            .OrderBy(screen => screen.Bounds.Left)
            .ThenBy(screen => screen.Bounds.Top)
            .Select((screen, index) => CaptureRectangle(screen.Bounds, $"monitor-{index + 1}", prune: false))
            .ToArray();
        _history.Prune(results.Select(result => result.FilePath));
        return results;
    }

    public ScreenshotResult CaptureActiveWindow(IntPtr fallbackWindow = default)
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (fallbackWindow != IntPtr.Zero && NativeMethods.IsWindow(fallbackWindow))
        {
            handle = fallbackWindow;
        }

        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("No active window is available to capture.");
        }

        var result = NativeMethods.DwmGetWindowAttribute(
            handle,
            NativeMethods.DwmwaExtendedFrameBounds,
            out var bounds,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeRect>());

        if (result != 0 && !NativeMethods.GetWindowRect(handle, out bounds))
        {
            throw new InvalidOperationException("The active window bounds could not be read.");
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("The active window is minimized or unavailable.");
        }

        return CaptureRectangle(
            new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            "window");
    }

    public ScreenshotResult CaptureRegion(Rectangle region)
    {
        if (region.Width < 2 || region.Height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "The selected region is too small.");
        }

        return CaptureRectangle(region, "region");
    }

    private ScreenshotResult CaptureRectangle(Rectangle bounds, string source, bool prune = true)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        return _history.Save(bitmap, source, prune);
    }
}
