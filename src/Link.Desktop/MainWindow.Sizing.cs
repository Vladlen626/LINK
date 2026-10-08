using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Windows;

namespace Link.Desktop;

// Window size and state: the side borders set the width, the bottom border the height.
public partial class MainWindow
{
    // Until the bottom edge is dragged the answer stays compact and scrolls.
    private const double DefaultAnswerMaxHeight = 280;
    // Making Link shorter never squeezes the answer below a few readable lines.
    private const double MinimumAnswerHeight = 96;
    private bool _resizingHeight;
    private double _heightBeforeResize;
    private double _minimumDragHeight;

    private static double MaxWindowHeight =>
        Math.Clamp(SystemParameters.VirtualScreenHeight, WindowPlacement.MinHeight, WindowPlacement.MaxHeight);

    public void SaveWindowState()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Current.Window.Left = double.IsFinite(Left) ? Left : null;
            _settings.Current.Window.Top = double.IsFinite(Top) ? Top : null;
            if (double.IsFinite(ActualWidth) && ActualWidth > 0) _settings.Current.Window.Width = ActualWidth;
        }

        // The Studio's place is saved with the overlay's, as Link exits.
        _studio?.Remember();
        _settings.Save();
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            // Runs before the system sizing loop starts, which reads the height limits only once.
            case NativeMethods.WmNcLButtonDown when IsBottomEdge(wParam):
                BeginHeightResize();
                break;
            case NativeMethods.WmSizing when _resizingHeight:
                KeepMinimumHeight(lParam);
                break;
            case NativeMethods.WmExitSizeMove:
                EndSizeMove();
                break;
            case NativeMethods.WmNcLButtonDblClk when IsBottomEdge(wParam):
                handled = true;
                ResetHeightLimit();
                break;
        }

        return IntPtr.Zero;
    }

    private static bool IsBottomEdge(IntPtr hitTest) =>
        hitTest.ToInt32() is NativeMethods.HtBottom or NativeMethods.HtBottomLeft or NativeMethods.HtBottomRight;

    private void BeginHeightResize()
    {
        _resizingHeight = true;
        _heightBeforeResize = ActualHeight;
        _minimumDragHeight = Math.Min(ActualHeight, GetMinimumHeight());
        // Hold the current height, then let the drag pass the old limit with the answer growing along.
        SizeToContent = SizeToContent.Manual;
        Height = ActualHeight;
        MaxHeight = MaxWindowHeight;
        ResponsePanel.MaxHeight = double.PositiveInfinity;
    }

    private void KeepMinimumHeight(IntPtr lParam)
    {
        var bounds = Marshal.PtrToStructure<NativeRect>(lParam);
        var minimum = (int)Math.Ceiling(_minimumDragHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
        if (bounds.Height >= minimum) return;
        bounds.Bottom = bounds.Top + minimum;
        Marshal.StructureToPtr(bounds, lParam, false);
    }

    private void EndSizeMove()
    {
        // WPF switches SizeToContent to Manual as soon as a border is dragged; restore the chosen height mode.
        if (_resizingHeight)
        {
            _resizingHeight = false;
            // A click on the edge without dragging keeps the previous limit.
            if (Math.Abs(ActualHeight - _heightBeforeResize) >= 1) _settings.Current.Window.HeightLimit = ActualHeight;
        }

        ApplyHeight();
        SaveWindowState();
    }

    private void ResetHeightLimit()
    {
        if (_settings.Current.Window.HeightLimit is null) return;
        _settings.Current.Window.HeightLimit = null;
        ApplyHeight();
        SaveWindowState();
    }

    private double GetMinimumHeight()
    {
        // Prompt, context and status keep their natural height; only the answer area gives way.
        var controls = InputPanel.Children.Cast<UIElement>().Sum(child => child.DesiredSize.Height) +
                       StatusRow.DesiredSize.Height;
        var answer = ResponsePanel.Visibility == Visibility.Visible
            ? ResponsePanel.Margin.Top + MinimumAnswerHeight + RedirectPanel.DesiredSize.Height
            : 0;
        var frame = HeaderRow.ActualHeight + CardContent.Margin.Top + CardContent.Margin.Bottom +
                    ShellBorder.BorderThickness.Top + ShellBorder.BorderThickness.Bottom;
        return Math.Ceiling(frame + controls + answer);
    }

    private void RestoreWindowState()
    {
        var placement = _settings.Current.Window;
        MinWidth = WindowPlacement.MinWidth;
        MaxWidth = WindowPlacement.MaxWidth;
        Width = Math.Clamp(placement.Width, WindowPlacement.MinWidth, WindowPlacement.MaxWidth);
        ApplyHeight();
        if (placement.Left is { } left) Left = left;
        if (placement.Top is { } top) Top = top;
        WindowStyleService.EnsureVisible(this);
    }

    private void ApplyHeight()
    {
        // Without a chosen height Link fits its content with a compact answer. The height chosen with the
        // bottom edge is kept as is, even taller than the content: the answer fills it and scrolls.
        MaxHeight = MaxWindowHeight;
        if (_settings.Current.Window.HeightLimit is { } height)
        {
            ResponsePanel.MaxHeight = double.PositiveInfinity;
            SizeToContent = SizeToContent.Manual;
            Height = Math.Clamp(height, WindowPlacement.MinHeight, MaxWindowHeight);
        }
        else
        {
            ResponsePanel.MaxHeight = DefaultAnswerMaxHeight;
            ClearValue(HeightProperty);
            SizeToContent = SizeToContent.Height;
        }
    }
}
