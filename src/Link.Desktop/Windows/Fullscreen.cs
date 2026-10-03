using System.Runtime.InteropServices;

namespace Link.Desktop.Windows;

/// <summary>How a window fills its screen: a game or a video in full screen should not lose the focus to Link.</summary>
public enum FullscreenKind
{
    None,

    /// <summary>A frameless window over its whole monitor: Link may draw over it, but without taking the focus.</summary>
    Borderless,

    /// <summary>Direct3D owns the screen: a window drawn over it would throw the game out of full screen.</summary>
    Exclusive
}

public static class Fullscreen
{
    public static FullscreenKind Of(IntPtr window)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window) || !NativeMethods.GetWindowRect(window, out var bounds))
        {
            return FullscreenKind.None;
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return FullscreenKind.None;
        var style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle).ToInt64();
        // Windows reports an exclusive Direct3D app for the whole session; the window's own size says whether it is this one.
        var exclusive = NativeMethods.SHQueryUserNotificationState(out var state) == 0 && state == NativeMethods.QunsRunningD3dFullScreen;
        return Classify(bounds, info.Monitor, (style & NativeMethods.WsCaption) == NativeMethods.WsCaption, exclusive);
    }

    internal static FullscreenKind Classify(NativeRect window, NativeRect monitor, bool hasCaption, bool exclusiveSession)
    {
        var coversMonitor = window.Left <= monitor.Left && window.Top <= monitor.Top &&
                            window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
        if (!coversMonitor) return FullscreenKind.None;
        if (exclusiveSession) return FullscreenKind.Exclusive;
        return hasCaption ? FullscreenKind.None : FullscreenKind.Borderless;
    }
}
