using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private const double MinimumHeight = 520;
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

    /// <summary>A tab was clicked.</summary>
    public event Action<PanelTab>? TabChosen;

    /// <summary>The Studio button was clicked: the tasks are in the Studio, a window of its own.</summary>
    public event Action? StudioRequested;

    /// <summary>Shows a tab's content; the others stay one click away. The project's tab bears its name.</summary>
    public void ShowTab(PanelTab tab, string projectName, FrameworkElement content)
    {
        ProjectTabText.Text = projectName;
        ProjectTabButton.ToolTip = $"The project {projectName}: its note, workspace, map, skills and check";
        foreach (var (button, text, chosen) in new[]
                 {
                     (ChatsTabButton, ChatsTabText, tab == PanelTab.Chats),
                     (ProjectTabButton, ProjectTabText, tab == PanelTab.Project)
                 })
        {
            button.Background = chosen ? (Brush)FindResource("ActiveContextBrush") : Brushes.Transparent;
            text.Foreground = (Brush)FindResource(chosen ? "TextBrush" : "MutedBrush");
        }

        Body.Content = content;
    }

    /// <summary>The Studio button's dot: how the agent's task stands (working, needs you, done); null hides it.</summary>
    public void ShowTaskDot(Brush? dot)
    {
        TaskTabDot.Fill = dot;
        TaskTabDot.Visibility = dot is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChatsTab_OnClick(object sender, RoutedEventArgs e) => TabChosen?.Invoke(PanelTab.Chats);

    private void Studio_OnClick(object sender, RoutedEventArgs e) => StudioRequested?.Invoke();

    private void ProjectTab_OnClick(object sender, RoutedEventArgs e) => TabChosen?.Invoke(PanelTab.Project);

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

public enum PanelTab
{
    Chats,
    Project
}
