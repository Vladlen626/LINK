using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using Link.Desktop.Windows;

namespace Link.Desktop.Tests;

/// <summary>
/// What the model sees of the screen: a black frame is told from a dark one, a window over its whole monitor is told
/// from an ordinary one, and Windows.Graphics.Capture really returns a frame on this machine.
/// </summary>
internal static class CaptureTests
{
    public static async Task RunAsync()
    {
        Program.Equal("a black frame shows nothing", ScreenCaptureService.IsBlank(Filled(Color.Black)), true);
        Program.Equal("a see-through frame shows nothing", ScreenCaptureService.IsBlank(Filled(Color.Transparent)), true);
        Program.Equal("a dark frame shows something", ScreenCaptureService.IsBlank(Filled(Color.FromArgb(14, 14, 18))), false);
        using (var hud = Filled(Color.Black))
        {
            using (var graphics = Graphics.FromImage(hud)) graphics.FillRectangle(Brushes.White, 900, 500, 60, 40);
            Program.Equal("a black frame with a lit HUD shows something", ScreenCaptureService.IsBlank(hud), false);
        }

        var monitor = Rect(0, 0, 1920, 1080);
        Program.Equal("a frameless window over the whole monitor is borderless fullscreen",
            Fullscreen.Classify(Rect(0, 0, 1920, 1080), monitor, hasCaption: false, exclusiveSession: false), FullscreenKind.Borderless);
        Program.Equal("a maximized window with its title bar is no fullscreen",
            Fullscreen.Classify(Rect(-8, -8, 1928, 1088), monitor, hasCaption: true, exclusiveSession: false), FullscreenKind.None);
        Program.Equal("a window over the monitor while Direct3D owns the screen is exclusive",
            Fullscreen.Classify(Rect(0, 0, 1920, 1080), monitor, hasCaption: false, exclusiveSession: true), FullscreenKind.Exclusive);
        Program.Equal("a smaller window is no fullscreen even while a game owns the screen",
            Fullscreen.Classify(Rect(100, 100, 900, 700), monitor, hasCaption: false, exclusiveSession: true), FullscreenKind.None);

        // A real frame needs a desktop session with a GPU or WARP; CI has neither for sure.
        if (Environment.GetEnvironmentVariable("CI") is not null || !GraphicsCapture.IsSupported) return;
        var primary = NativeMethods.MonitorFromPoint(new NativePoint(), NativeMethods.MonitorDefaultToPrimary);
        try
        {
            using var frame = await GraphicsCapture.CaptureMonitorAsync(primary, TimeSpan.FromSeconds(3));
            Program.Record($"Windows.Graphics.Capture gives a frame of the primary monitor ({frame.Width}x{frame.Height})",
                frame.Width > 0 && frame.Height > 0);
        }
        catch (Exception exception)
        {
            Program.Record($"Windows.Graphics.Capture gives a frame of the primary monitor: {exception.GetType().Name} {exception.Message}", false);
        }
    }

    private static Bitmap Filled(Color color)
    {
        var bitmap = new Bitmap(1920, 1080, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static NativeRect Rect(int left, int top, int right, int bottom) =>
        new() { Left = left, Top = top, Right = right, Bottom = bottom };
}
