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
