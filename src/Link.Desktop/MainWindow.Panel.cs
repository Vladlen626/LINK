using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Link.Desktop;

// The panel beside the card: Expand opens it, it follows the card and hides and shows with it.
public partial class MainWindow
{
    private SidePanelWindow? _panel;

    private bool IsLinkActive => IsActive || _panel?.IsActive == true;

    private void Expand_OnClick(object sender, RoutedEventArgs e) => SetPanelOpen(!_settings.Current.SidePanel.IsOpen);

    private void SetPanelOpen(bool open)
    {
        _settings.Current.SidePanel.IsOpen = open;
        _settings.Save();
        UpdateExpandButton();
        if (open) ShowPanel();
        else _panel?.Hide();
    }

    private void UpdateExpandButton()
    {
        var open = _settings.Current.SidePanel.IsOpen;
        ExpandIcon.Stroke = (Brush)FindResource(open ? "AccentBrush" : "TextBrush");
        ExpandButton.ToolTip = open ? "Close the panel" : "Open the project panel beside Link";
    }

    private void ShowPanel()
    {
        if (!IsVisible || !_settings.Current.SidePanel.IsOpen) return;
        _panel ??= CreatePanel();
        _panel.PlaceBeside(this, _settings.Current.SidePanel.Width);
        _panel.Show();
    }

    private SidePanelWindow CreatePanel()
    {
        var panel = new SidePanelWindow { Owner = this, Topmost = Topmost };
        // Placed and glazed before it is first shown, so it never flashes elsewhere.
        new WindowInteropHelper(panel).EnsureHandle();
        ApplyGlass(panel, panel.ShellBorder);
        panel.SetContent(_projects.ActiveProject.Name, new Grid());
        // Exiting Link closes the panel too; it stays open for the next start.
        panel.CloseRequested += () =>
        {
            if (!AllowClose) SetPanelOpen(false);
        };
        panel.WidthChosen += width =>
        {
            _settings.Current.SidePanel.Width = width;
            _settings.Save();
            PlacePanel();
        };
        panel.DpiChanged += (_, _) => PlacePanel();
        return panel;
    }

    private void PlacePanel()
    {
        if (_panel is { IsVisible: true }) _panel.PlaceBeside(this, _settings.Current.SidePanel.Width);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) _panel?.Hide();
        // Link is drawn first and its panel a moment later, so showing Link is not held up by it.
        else Dispatcher.BeginInvoke(ShowPanel, DispatcherPriority.Background);
    }
}
