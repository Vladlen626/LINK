using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Link.Desktop.Windows;

public static class WindowStyleService
{
    // The overlay is a layered window: DWM system backdrops are milky and cannot be tuned, while the
    // accent blur only softens what shows through the window's own translucent pixels.
    public static bool EnableGlass(Window window, bool blur)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        try
        {
            return SetAccent(handle, new AccentPolicy
            {
                AccentState = blur ? NativeMethods.AccentEnableBlurBehind : NativeMethods.AccentDisabled
            });
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // Dialogs keep the standard caption and show the system acrylic behind their transparent client area.
    public static void EnableBackdrop(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            window.Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            {
                target.BackgroundColor = Colors.Transparent;
            }

            var margins = new DwmMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);

            var darkMode = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(
                handle,
                NativeMethods.DwmwaUseImmersiveDarkMode,
                ref darkMode,
                Marshal.SizeOf<int>());

            if (Environment.OSVersion.Version.Build >= 22000)
            {
                var backdropType = NativeMethods.DwmSystemBackdropTypeTransientWindow;
                var result = NativeMethods.DwmSetWindowAttribute(
                    handle,
                    NativeMethods.DwmwaSystemBackdropType,
                    ref backdropType,
                    Marshal.SizeOf<int>());
                if (result == 0) return;
            }

            SetAccent(handle, new AccentPolicy
            {
                AccentState = NativeMethods.AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                // AccentPolicy uses AABBGGRR.
                GradientColor = unchecked((int)(dark ? 0xB0201A17 : 0x78FCF7E8))
            });
        }
        catch (DllNotFoundException)
        {
            // The XAML tint remains the compatibility fallback.
        }
        catch (EntryPointNotFoundException)
        {
            // The XAML tint remains the compatibility fallback.
        }
    }

    private static bool SetAccent(IntPtr handle, AccentPolicy accent)
    {
        var size = Marshal.SizeOf<AccentPolicy>();
        var accentPointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, accentPointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WcaAccentPolicy,
                Data = accentPointer,
                SizeOfData = size
            };
            return NativeMethods.SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPointer);
        }
    }

    /// <summary>
    /// Keeps a helper window off the taskbar and Alt+Tab. With <paramref name="noActivate"/> neither a click nor the
    /// system makes it the active window, so a page working in it cannot take the keyboard from the user.
    /// </summary>
    public static void SetToolWindow(Window window, bool noActivate)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64() | NativeMethods.WsExToolWindow;
        style = noActivate ? style | NativeMethods.WsExNoActivate : style & ~NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
    }

    /// <summary>
    /// Places a helper window as a frame of <paramref name="thickness"/> pixels around another window, on top of it and
    /// without taking the focus: a region cuts the window down to the frame, so nothing but the frame is drawn.
    /// Nothing happens when the other window's bounds cannot be read.
    /// </summary>
    public static void FrameAround(Window frame, IntPtr target, int thickness)
    {
        var handle = new WindowInteropHelper(frame).Handle;
        if (handle == IntPtr.Zero ||
            NativeMethods.DwmGetWindowAttribute(target, NativeMethods.DwmwaExtendedFrameBounds, out var bounds,
                Marshal.SizeOf<NativeRect>()) != 0 && !NativeMethods.GetWindowRect(target, out bounds))
        {
            return;
        }

        var width = bounds.Width + 2 * thickness;
        var height = bounds.Height + 2 * thickness;
        var outer = NativeMethods.CreateRectRgn(0, 0, width, height);
        var inner = NativeMethods.CreateRectRgn(thickness, thickness, width - thickness, height - thickness);
        NativeMethods.CombineRgn(outer, outer, inner, NativeMethods.RgnDiff);
        NativeMethods.DeleteObject(inner);
        // The window owns the region from here on.
        if (NativeMethods.SetWindowRgn(handle, outer, true) == 0) NativeMethods.DeleteObject(outer);
        NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, bounds.Left - thickness, bounds.Top - thickness, width, height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    /// <summary>Moves a window just past the right edge of all monitors: shown as far as Windows knows, seen by nobody.</summary>
    public static void MoveOffScreen(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var right = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen) +
                    NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            right + 64,
            NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>Centers a window on the monitor <paramref name="anchor"/> is on, within that monitor's work area.</summary>
    public static void CenterOnMonitorOf(Window window, Window? anchor)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !NativeMethods.GetWindowRect(handle, out var bounds)) return;
        var anchorHandle = anchor is null ? IntPtr.Zero : new WindowInteropHelper(anchor).Handle;
        var monitor = NativeMethods.MonitorFromWindow(
            anchorHandle,
            anchorHandle == IntPtr.Zero ? NativeMethods.MonitorDefaultToPrimary : NativeMethods.MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;

        const int margin = 24;
        var work = info.Work;
        var width = Math.Min(bounds.Width, work.Width - 2 * margin);
        var height = Math.Min(bounds.Height, work.Height - 2 * margin);
        NativeMethods.SetWindowPos(
            handle,
            IntPtr.Zero,
            work.Left + (work.Width - width) / 2,
            work.Top + (work.Height - height) / 2,
            width,
            height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>
    /// Moves <paramref name="panel"/> beside <paramref name="card"/>, top-aligned with it and inside the work area of
    /// the card's monitor: on the preferred side while it fits there, otherwise on the side with more room, over the
    /// card's edge when neither has enough. Sizes are device-independent pixels; returns whether it is on the right.
    /// </summary>
    public static bool PlaceBeside(Window panel, Window card, double width, double minimumHeight, bool? preferRight)
    {
        const double gap = 6;
        var panelHandle = new WindowInteropHelper(panel).Handle;
        var cardHandle = new WindowInteropHelper(card).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (panelHandle == IntPtr.Zero || !NativeMethods.GetWindowRect(cardHandle, out var bounds) ||
            !NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(cardHandle, NativeMethods.MonitorDefaultToNearest), ref info))
        {
            return preferRight ?? true;
        }

        // Device pixels at the card's scale: the panel always stays on the card's monitor.
        var scale = VisualTreeHelper.GetDpi(card).DpiScaleX;
        var work = info.Work;
        var space = (int)Math.Round(gap * scale);
        var panelWidth = Math.Min((int)Math.Round(width * scale), work.Width);
        var height = Math.Min(Math.Max(bounds.Height, (int)Math.Round(minimumHeight * scale)), work.Height);
        var roomRight = work.Right - bounds.Right - space;
        var roomLeft = bounds.Left - space - work.Left;
        var onRight = preferRight switch
        {
            true when roomRight >= panelWidth => true,
            false when roomLeft >= panelWidth => false,
            _ => roomRight >= roomLeft
        };
        var left = Math.Clamp(onRight ? bounds.Right + space : bounds.Left - space - panelWidth, work.Left, work.Right - panelWidth);
        var top = Math.Clamp(bounds.Top, work.Top, work.Bottom - height);
        NativeMethods.SetWindowPos(panelHandle, IntPtr.Zero, left, top, panelWidth, height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        return onRight;
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

[StructLayout(LayoutKind.Sequential)]
internal struct AccentPolicy
{
    public int AccentState;
    public int AccentFlags;
    public int GradientColor;
    public int AnimationId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowCompositionAttributeData
{
    public int Attribute;
    public IntPtr Data;
    public int SizeOfData;
}
