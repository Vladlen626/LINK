using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// A short outline around the window Link has just captured, the way Windows outlines a window shared with Copilot:
/// the user sees which window went with the question. An ordinary window cut down to a frame, not a layered one, so
/// it costs next to nothing; it never takes the focus and closes by itself.
/// </summary>
internal sealed class CaptureOutline : Window
{
    private const int Thickness = 3;
    private static readonly TimeSpan Shown = TimeSpan.FromMilliseconds(600);

    private CaptureOutline(Brush brush)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = brush;
        // Placed in pixels once it has a window handle; until then it waits off screen.
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
    }

    public static void Flash(IntPtr target, Brush brush)
    {
        var outline = new CaptureOutline(brush);
        // A window whose bounds cannot be read gets no frame: the outline stays off screen until it closes.
        outline.SourceInitialized += (_, _) =>
        {
            WindowStyleService.SetToolWindow(outline, noActivate: true);
            WindowStyleService.FrameAround(outline, target, Thickness);
        };
        outline.Show();
        var timer = new DispatcherTimer { Interval = Shown };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            outline.Close();
        };
        timer.Start();
    }
}
