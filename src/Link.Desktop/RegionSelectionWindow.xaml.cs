using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class RegionSelectionWindow : Window
{
    private NativePoint _start;
    private bool _selecting;

    public RegionSelectionWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Loaded += (_, _) => Focus();
    }

    public System.Drawing.Rectangle? SelectedRegion { get; private set; }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        NativeMethods.GetCursorPos(out _start);
        _selecting = true;
        SelectionBorder.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateSelection(_start);
    }

    private void Window_OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_selecting && NativeMethods.GetCursorPos(out var current)) UpdateSelection(current);
    }

    private void Window_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting || !NativeMethods.GetCursorPos(out var end)) return;
        _selecting = false;
        ReleaseMouseCapture();

        var left = Math.Min(_start.X, end.X);
        var top = Math.Min(_start.Y, end.Y);
        var width = Math.Abs(end.X - _start.X);
        var height = Math.Abs(end.Y - _start.Y);
        if (width >= 4 && height >= 4)
        {
            SelectedRegion = new System.Drawing.Rectangle(left, top, width, height);
            DialogResult = true;
        }
        else
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSelection(NativePoint current)
    {
        var start = PointFromScreen(new Point(_start.X, _start.Y));
        var end = PointFromScreen(new Point(current.X, current.Y));
        Canvas.SetLeft(SelectionBorder, Math.Min(start.X, end.X));
        Canvas.SetTop(SelectionBorder, Math.Min(start.Y, end.Y));
        SelectionBorder.Width = Math.Abs(end.X - start.X);
        SelectionBorder.Height = Math.Abs(end.Y - start.Y);
    }

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
