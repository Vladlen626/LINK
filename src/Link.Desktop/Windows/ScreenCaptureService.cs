using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Link.Desktop.Services;
using BitmapSource = System.Windows.Media.Imaging.BitmapSource;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace Link.Desktop.Windows;

/// <summary>
/// Screenshots for the context: through Windows.Graphics.Capture first, which sees games and GPU-drawn pages and a
/// window under Link's overlay, then the older ways. A black frame is refused, not sent as if it showed something.
/// The work runs off the UI thread; each capture leaves a [Capture] line in link.log, without window titles.
/// </summary>
public sealed class ScreenCaptureService
{
    // A frame comes with the next refresh; one that does not come in this time will not come.
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(1.5);
    private const string BlackFrame =
        "The capture came out black: a game in exclusive fullscreen or protected video. Switch the game to borderless, or ask by voice.";
    private readonly ScreenshotHistoryService _history;
    private readonly Action<string> _log;

    public ScreenCaptureService(ScreenshotHistoryService history, Action<string> log)
    {
        _history = history;
        _log = log;
    }

    public Task<ScreenshotResult> CaptureCurrentMonitorAsync()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var screen = System.Windows.Forms.Screen.FromPoint(new Point(cursor.X, cursor.Y));
        return Task.Run(() => Save(CaptureMonitor(screen, "monitor"), "monitor", prune: true));
    }

    public Task<ScreenshotResult> CaptureDesktopAsync() => Task.Run(() =>
    {
        var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        var whole = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(whole))
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                using var part = CaptureMonitor(screen, "desktop");
                graphics.DrawImageUnscaled(part, screen.Bounds.Left - desktop.Left, screen.Bounds.Top - desktop.Top);
            }
        }

        return Save(whole, "desktop", prune: true);
    });

    public Task<IReadOnlyList<ScreenshotResult>> CaptureAllMonitorsAsync() => Task.Run<IReadOnlyList<ScreenshotResult>>(() =>
    {
        var results = System.Windows.Forms.Screen.AllScreens
            .OrderBy(screen => screen.Bounds.Left)
            .ThenBy(screen => screen.Bounds.Top)
            .Select((screen, index) => Save(CaptureMonitor(screen, $"monitor {index + 1}"), $"monitor-{index + 1}", prune: false))
            .ToArray();
        _history.Prune(results.Select(result => result.FilePath));
        return results;
    });

    /// <summary>A window as it draws itself, even under other windows; Link need not hide for it.</summary>
    public Task<ScreenshotResult> CaptureWindowAsync(IntPtr handle, bool prune = true)
    {
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
        {
            throw new InvalidOperationException("The window is no longer available to capture.");
        }

        if (NativeMethods.IsIconic(handle)) throw new InvalidOperationException("The window is minimized.");
        return Task.Run(() => Save(CaptureWindow(handle), "window", prune));
    }

    public Task<ScreenshotResult> CaptureRegionAsync(Rectangle region)
    {
        if (region.Width < 2 || region.Height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "The selected region is too small.");
        }

        return Task.Run(() => Save(CaptureRegion(region), "region", prune: true));
    }

    /// <summary>
    /// A small picture of a window for the card, kept in memory only: it is neither saved nor sent. Null when the
    /// window cannot be captured this way.
    /// </summary>
    public Task<BitmapSource?> PreviewWindowAsync(IntPtr handle, int width) => Task.Run(() =>
    {
        if (!GraphicsCapture.IsSupported || !NativeMethods.IsWindow(handle) || NativeMethods.IsIconic(handle)) return null;
        try
        {
            using var frame = GraphicsCapture.CaptureWindowAsync(handle, FrameTimeout).GetAwaiter().GetResult();
            if (IsBlank(frame)) return null;
            var height = Math.Max(1, frame.Height * width / Math.Max(1, frame.Width));
            using var small = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(small))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(frame, 0, 0, width, height);
            }

            var data = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, data.Scan0,
                    data.Stride * height, data.Stride);
                picture.Freeze();
                return picture;
            }
            finally
            {
                small.UnlockBits(data);
            }
        }
        catch (Exception exception) when (IsCaptureFailure(exception))
        {
            return null;
        }
    });

    /// <summary>
    /// Whether a frame shows nothing: every sampled pixel black or see-through. What a game in exclusive fullscreen or
    /// protected video gives a capture. A dark scene still has its lit pixels, a HUD or a cursor of its own.
    /// </summary>
    internal static bool IsBlank(Bitmap bitmap)
    {
        const int Samples = 128;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[data.Stride];
            for (var sampleY = 0; sampleY < Samples; sampleY++)
            {
                var y = (int)((sampleY + 0.5) * bitmap.Height / Samples);
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (var sampleX = 0; sampleX < Samples; sampleX++)
                {
                    var x = (int)((sampleX + 0.5) * bitmap.Width / Samples) * 4;
                    // Blue, green, red, alpha; premultiplied, so a see-through pixel is black too.
                    if (Math.Max(row[x], Math.Max(row[x + 1], row[x + 2])) > 6) return false;
                }
            }

            return true;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private Bitmap CaptureWindow(IntPtr handle)
    {
        var clock = Stopwatch.StartNew();
        var tried = new List<string>();
        if (GraphicsCapture.IsSupported)
        {
            try
            {
                var frame = GraphicsCapture.CaptureWindowAsync(handle, FrameTimeout).GetAwaiter().GetResult();
                if (!IsBlank(frame)) return Logged(frame, "window", "wgc", tried, clock);
                frame.Dispose();
                tried.Add("wgc black");
            }
            catch (Exception exception) when (IsCaptureFailure(exception))
            {
                tried.Add($"wgc failed ({exception.GetType().Name})");
            }
        }

        var printed = PrintWindow(handle);
        if (!IsBlank(printed)) return Logged(printed, "window", "printwindow", tried, clock);
        printed.Dispose();
        tried.Add("printwindow black");
        _log($"[Capture] window: {string.Join(", ", tried)}, {clock.ElapsedMilliseconds} ms");
        throw new InvalidOperationException(BlackFrame);
    }

    private Bitmap CaptureMonitor(System.Windows.Forms.Screen screen, string what)
    {
        var clock = Stopwatch.StartNew();
        var tried = new List<string>();
        var bounds = screen.Bounds;
        if (GraphicsCapture.IsSupported)
        {
            try
            {
                var center = new NativePoint { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
                var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MonitorDefaultToNearest);
                var frame = GraphicsCapture.CaptureMonitorAsync(monitor, FrameTimeout).GetAwaiter().GetResult();
                if (frame.Width == bounds.Width && frame.Height == bounds.Height && !IsBlank(frame))
                {
                    return Logged(frame, what, "wgc", tried, clock);
                }

                frame.Dispose();
                tried.Add("wgc black or resized");
            }
            catch (Exception exception) when (IsCaptureFailure(exception))
            {
                tried.Add($"wgc failed ({exception.GetType().Name})");
            }
        }

        var copied = CopyFromScreen(bounds);
        if (!IsBlank(copied)) return Logged(copied, what, "gdi", tried, clock);
        copied.Dispose();
        tried.Add("gdi black");
        _log($"[Capture] {what}: {string.Join(", ", tried)}, {clock.ElapsedMilliseconds} ms");
        throw new InvalidOperationException(BlackFrame);
    }

    /// <summary>A region within one monitor comes from that monitor's frame; one across monitors from the screen.</summary>
    private Bitmap CaptureRegion(Rectangle region)
    {
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(screen => screen.Bounds.Contains(region));
        if (screen is null) return Checked(CopyFromScreen(region), "region");
        using var monitor = CaptureMonitor(screen, "region");
        var part = new Rectangle(region.Left - screen.Bounds.Left, region.Top - screen.Bounds.Top, region.Width, region.Height);
        return monitor.Clone(part, PixelFormat.Format32bppPArgb);
    }

    private Bitmap Checked(Bitmap bitmap, string what)
    {
        if (!IsBlank(bitmap))
        {
            _log($"[Capture] {what}: gdi {bitmap.Width}x{bitmap.Height}");
            return bitmap;
        }

        bitmap.Dispose();
        _log($"[Capture] {what}: gdi black");
        throw new InvalidOperationException(BlackFrame);
    }

    private Bitmap Logged(Bitmap bitmap, string what, string way, List<string> tried, Stopwatch clock)
    {
        tried.Add($"{way} {bitmap.Width}x{bitmap.Height}");
        _log($"[Capture] {what}: {string.Join(", ", tried)}, {clock.ElapsedMilliseconds} ms");
        return bitmap;
    }

    private ScreenshotResult Save(Bitmap bitmap, string source, bool prune)
    {
        using (bitmap) return _history.Save(bitmap, source, prune);
    }

    private static Bitmap CopyFromScreen(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// The window draws itself into Link's bitmap, GPU content included where the app supports it. It draws its whole
    /// rectangle, invisible resize borders too; the visible frame is cut out of that.
    /// </summary>
    private static Bitmap PrintWindow(IntPtr handle)
    {
        if (!NativeMethods.GetWindowRect(handle, out var outer) || outer.Width <= 0 || outer.Height <= 0)
        {
            throw new InvalidOperationException("The window is minimized or unavailable.");
        }

        var frame = NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DwmwaExtendedFrameBounds, out var visible,
            Marshal.SizeOf<NativeRect>()) == 0 ? visible : outer;
        using var whole = new Bitmap(outer.Width, outer.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(whole))
        {
            var hdc = graphics.GetHdc();
            try
            {
                NativeMethods.PrintWindow(handle, hdc, NativeMethods.PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        var cut = Rectangle.Intersect(
            new Rectangle(frame.Left - outer.Left, frame.Top - outer.Top, frame.Width, frame.Height),
            new Rectangle(0, 0, whole.Width, whole.Height));
        return whole.Clone(cut.IsEmpty ? new Rectangle(0, 0, whole.Width, whole.Height) : cut, PixelFormat.Format32bppPArgb);
    }

    private static bool IsCaptureFailure(Exception exception) =>
        exception is COMException or InvalidOperationException or TimeoutException or ArgumentException or UnauthorizedAccessException;
}
