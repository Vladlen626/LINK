using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Link.Desktop.Models;
using Link.Desktop.Windows;

namespace Link.Desktop;

// Window size and state: the side borders set the width, the bottom border the height,
// and the collapsed capsule keeps a fixed size.
public partial class MainWindow
{
    // Until the bottom edge is dragged the answer stays compact and scrolls.
    private const double DefaultAnswerMaxHeight = 280;
    // Making Link shorter never squeezes the answer below a few readable lines.
    private const double MinimumAnswerHeight = 96;
    private readonly Thickness _resizeBorder;
    private bool _resizingHeight;
    private double _heightBeforeResize;
    private double _minimumDragHeight;

    private static double MaxExpandedHeight =>
        Math.Clamp(SystemParameters.VirtualScreenHeight, WindowPlacement.MinHeight, WindowPlacement.MaxHeight);

    public void SaveWindowState()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Current.Window.Left = double.IsFinite(Left) ? Left : null;
            _settings.Current.Window.Top = double.IsFinite(Top) ? Top : null;
            var width = _collapsed ? _expandedWidth : ActualWidth;
            if (double.IsFinite(width) && width > 0) _settings.Current.Window.Width = width;
            _settings.Current.IsCollapsed = _collapsed;
        }

        _settings.Save();
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_collapsed) return IntPtr.Zero;

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
        _minimumDragHeight = Math.Min(ActualHeight, GetMinimumExpandedHeight());
        // Hold the current height, then let the drag pass the old limit with the answer growing along.
        SizeToContent = SizeToContent.Manual;
        Height = ActualHeight;
        MaxHeight = MaxExpandedHeight;
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
        _expandedWidth = Math.Clamp(ActualWidth, WindowPlacement.MinWidth, WindowPlacement.MaxWidth);
        if (_resizingHeight)
        {
            _resizingHeight = false;
            // A click on the edge without dragging keeps the previous limit.
            if (Math.Abs(ActualHeight - _heightBeforeResize) >= 1) _settings.Current.Window.HeightLimit = ActualHeight;
        }

        ApplyExpandedHeight();
        SaveWindowState();
    }

    private void ResetHeightLimit()
    {
        if (_settings.Current.Window.HeightLimit is null) return;
        _settings.Current.Window.HeightLimit = null;
        ApplyExpandedHeight();
        SaveWindowState();
    }

    private double GetMinimumExpandedHeight()
    {
        // Prompt, context and status keep their natural height; only the answer area gives way.
        var controls = InputPanel.Children.Cast<UIElement>().Sum(child => child.DesiredSize.Height) +
                       StatusRow.DesiredSize.Height;
        var answer = ResponsePanel.Visibility == Visibility.Visible ? ResponsePanel.Margin.Top + MinimumAnswerHeight : 0;
        var frame = HeaderRow.ActualHeight + ExpandedContent.Margin.Top + ExpandedContent.Margin.Bottom +
                    ShellBorder.BorderThickness.Top + ShellBorder.BorderThickness.Bottom;
        return Math.Ceiling(frame + controls + answer);
    }

    private void RestoreWindowState()
    {
        var placement = _settings.Current.Window;
        _expandedWidth = Math.Clamp(placement.Width, WindowPlacement.MinWidth, WindowPlacement.MaxWidth);
        MinWidth = WindowPlacement.MinWidth;
        MaxWidth = WindowPlacement.MaxWidth;
        Width = _expandedWidth;
        ApplyExpandedHeight();
        if (placement.Left is { } left) Left = left;
        if (placement.Top is { } top) Top = top;
        WindowStyleService.EnsureVisible(this);
    }

    private void ApplyExpandedHeight()
    {
        // Without a chosen height Link fits its content with a compact answer. The height chosen with the
        // bottom edge is kept as is, even taller than the content: the answer fills it and scrolls.
        MaxHeight = MaxExpandedHeight;
        if (_settings.Current.Window.HeightLimit is { } height)
        {
            ResponsePanel.MaxHeight = double.PositiveInfinity;
            SizeToContent = SizeToContent.Manual;
            Height = Math.Clamp(height, WindowPlacement.MinHeight, MaxExpandedHeight);
        }
        else
        {
            ResponsePanel.MaxHeight = DefaultAnswerMaxHeight;
            ClearValue(HeightProperty);
            SizeToContent = SizeToContent.Height;
        }
    }

    private void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;
        // The capsule has a fixed size, so its edges stay part of the drag area.
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness = collapsed ? new Thickness(0) : _resizeBorder;

        if (collapsed)
        {
            _expandedWidth = Math.Clamp(ActualWidth, WindowPlacement.MinWidth, WindowPlacement.MaxWidth);
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedHeader.Visibility = Visibility.Collapsed;
            ResizeHandle.Visibility = Visibility.Collapsed;
            CollapsedHeader.Visibility = Visibility.Visible;
            HeaderRow.Height = new GridLength(46);
            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.NoResize;

            MinWidth = 0;
            MaxWidth = WindowPlacement.MaxWidth;
            Width = 180;
            MinWidth = 180;
            MaxWidth = 180;
            MinHeight = 0;
            MaxHeight = MaxExpandedHeight;
            Height = 46;
            MinHeight = 46;
            MaxHeight = 46;
            FadeIn(CollapsedHeader);
        }
        else
        {
            MinWidth = 0;
            MaxWidth = WindowPlacement.MaxWidth;
            MinHeight = 0;
            MaxHeight = MaxExpandedHeight;
            Width = _expandedWidth;
            ApplyExpandedHeight();
            ResizeMode = ResizeMode.CanResize;
            MinWidth = WindowPlacement.MinWidth;

            ExpandedContent.Visibility = Visibility.Visible;
            ExpandedHeader.Visibility = Visibility.Visible;
            ResizeHandle.Visibility = Visibility.Visible;
            CollapsedHeader.Visibility = Visibility.Collapsed;
            HeaderRow.Height = new GridLength(46);
            FadeIn(ExpandedContent);
        }

        _settings.Current.IsCollapsed = collapsed;
        ApplyClickThrough();
    }

    private static void FadeIn(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
