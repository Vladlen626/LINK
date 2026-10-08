using System.Windows;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// Link Studio: the agents' tasks in the middle (the <see cref="AgentPanel"/> it is given), the Harness Inspector on the
/// right, and in the header the open task's context with Compact. A normal window, made when it is first opened and gone
/// when it is closed: it keeps no timer, and gives the tasks back so that the next Studio can host them.
/// </summary>
public partial class StudioWindow : Window
{
    private const double InspectorWidth = 320;
    private readonly StudioSettings _settings;

    public StudioWindow(StudioSettings settings, AgentPanel tasks)
    {
        _settings = settings;
        InitializeComponent();
        TasksHost.Child = tasks;
        Inspector.OpenFileRequested += path => OpenFileRequested?.Invoke(path);
        Inspector.ConsoleRequested += () => ConsoleRequested?.Invoke();
        Width = settings.Width;
        Height = settings.Height;
        if (settings.Left is { } left && settings.Top is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            // A monitor that is gone since: the window comes up centered.
            WindowStyleService.EnsureVisible(this);
        }

        if (settings.Maximized) WindowState = WindowState.Maximized;
        ShowInspector(settings.InspectorOpen);
        SourceInitialized += (_, _) => WindowStyleService.EnableBackdrop(this, ThemeService.IsDark);
        Closing += (_, _) =>
        {
            Remember();
            // The tasks outlive the window: the next one hosts them again, and a control has only one parent.
            TasksHost.Child = null;
        };
    }

    /// <summary>The header's Compact: a fresh session of the open task's agent is to be started from its state.</summary>
    public event Action? CompactRequested;

    /// <summary>The inspector column opened, empty or with an old view: it is to be shown the harness as it stands.</summary>
    public event Action? InspectorOpened;

    /// <summary>A file of the harness is to be shown in Explorer.</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>The Console is to be opened.</summary>
    public event Action? ConsoleRequested;

    public bool InspectorOpen => _settings.InspectorOpen;

    /// <summary>The project, the open task's context line (null: no task is open) and whether Compact is offered.</summary>
    public void ShowHeader(string project, string? context, bool canCompact)
    {
        ProjectText.Text = project;
        ProjectText.ToolTip = project;
        ContextText.Text = context ?? string.Empty;
        ContextPanel.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        CompactButton.IsEnabled = canCompact;
    }

    public void ShowHarness(HarnessView view) => Inspector.Show(view);

    /// <summary>Opens or folds the inspector column, and keeps the choice.</summary>
    public void ShowInspector(bool open)
    {
        _settings.InspectorOpen = open;
        InspectorColumn.Width = new GridLength(open ? InspectorWidth : 0);
        InspectorScroll.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        HarnessButtonText.Foreground = (Brush)FindResource(open ? "AccentBrush" : "TextBrush");
        HarnessButton.ToolTip = open ? "Fold the Harness column" : "Show the Harness column: what the agent really gets, and why it behaves so";
        if (open) InspectorOpened?.Invoke();
    }

    /// <summary>Writes where the window stands to its settings, its normal bounds when it is maximized; the owner saves them.</summary>
    public void Remember()
    {
        var bounds = RestoreBounds;
        if (bounds.IsEmpty) return;
        _settings.Remember(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
    }

    private void Compact_OnClick(object sender, RoutedEventArgs e) => CompactRequested?.Invoke();

    private void Harness_OnClick(object sender, RoutedEventArgs e) => ShowInspector(!_settings.InspectorOpen);
}
