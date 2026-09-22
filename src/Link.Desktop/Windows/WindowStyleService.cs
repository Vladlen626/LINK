using System.Windows;
using System.Windows.Interop;

namespace Link.Desktop.Windows;

public static class WindowStyleService
{
    public static void SetClickThrough(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        var next = enabled ? style | NativeMethods.WsExTransparent : style & ~NativeMethods.WsExTransparent;
        if (next != style)
        {
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(next));
        }
    }

    public static void EnsureVisible(Window window)
    {
        const double visibleEdge = 48;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        if (double.IsNaN(window.Left) || window.Left + visibleEdge > virtualRight ||
            window.Left + window.Width - visibleEdge < virtualLeft)
        {
            window.Left = virtualLeft + Math.Max(16, (SystemParameters.VirtualScreenWidth - window.Width) / 2);
        }

        if (double.IsNaN(window.Top) || window.Top + visibleEdge > virtualBottom ||
            window.Top + visibleEdge < virtualTop)
        {
            window.Top = virtualTop + Math.Max(16, (SystemParameters.VirtualScreenHeight - window.Height) / 2);
        }
    }
}
