using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Link.Desktop.Windows;

public static class WindowStyleService
{
    public static void EnableBackdrop(Window window, double tintOpacity = 0.72)
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

            var cornerPreference = NativeMethods.DwmWindowCornerPreferenceRoundSmall;
            NativeMethods.DwmSetWindowAttribute(
                handle,
                NativeMethods.DwmwaWindowCornerPreference,
                ref cornerPreference,
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

            EnableAcrylicFallback(handle, tintOpacity);
        }
        catch (DllNotFoundException)
        {
            // The XAML material remains the compatibility fallback.
        }
        catch (EntryPointNotFoundException)
        {
            // The XAML material remains the compatibility fallback.
        }
    }

    private static void EnableAcrylicFallback(IntPtr handle, double tintOpacity)
    {
        var alpha = (byte)Math.Clamp(42 + (tintOpacity * 82), 42, 124);
        var accent = new AccentPolicy
        {
            AccentState = NativeMethods.AccentEnableAcrylicBlurBehind,
            AccentFlags = 2,
            // AccentPolicy uses AABBGGRR. Keep the native tint translucent so text remains fully opaque.
            GradientColor = unchecked((int)(((uint)alpha << 24) | 0x00FCF7E8))
        };
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
            NativeMethods.SetWindowCompositionAttribute(handle, ref data);
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
