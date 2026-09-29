using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Link.Desktop.Models;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// The panel beside Link. It is a window of its own, so the card's layered surface does not grow, and the card places
/// and moves it. It shows one panel at a time and appears without taking the focus.
/// </summary>
public partial class SidePanelWindow : Window
{
    // Shorter than this the panel's sections would not fit: beside a compact card it reaches below it.
    private const double MinimumHeight = 440;
    private const double ResizeEdge = 6;
    private bool? _onRight;

    public SidePanelWindow()
    {
        InitializeComponent();
        MinWidth = SidePanelSettings.MinWidth;
        MaxWidth = SidePanelSettings.MaxWidth;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProcedure);
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    /// <summary>The user closed the panel: its button, Esc or Alt+F4.</summary>
    public event Action? CloseRequested;

    /// <summary>The user dragged the outer edge to this width, in device-independent pixels.</summary>
    public event Action<double>? WidthChosen;

    public void SetContent(string title, FrameworkElement content)
    {
        TitleText.Text = title;
        Body.Content = content;
    }

    /// <summary>
    /// Puts the panel beside the card on the card's monitor. It keeps its side while it fits there and otherwise goes
    /// where the work area has more room; the card itself never moves.
    /// </summary>
    public void PlaceBeside(Window card, double width)
    {
        var onRight = WindowStyleService.PlaceBeside(this, card, width, MinimumHeight, _onRight);
        if (onRight == _onRight) return;
        _onRight = onRight;
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness =
            onRight ? new Thickness(0, 0, ResizeEdge, 0) : new Thickness(ResizeEdge, 0, 0, 0);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmExitSizeMove) WidthChosen?.Invoke(ActualWidth);
        return IntPtr.Zero;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseRequested?.Invoke();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Only hidden: the card shows it again. When Link exits, the cancel is ignored.
        e.Cancel = true;
        CloseRequested?.Invoke();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
