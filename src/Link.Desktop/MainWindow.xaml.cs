using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ProjectService _projects;
    private readonly RequestService _requests;
    private readonly ScreenCaptureService _capture;
    private readonly ClipboardContextService _clipboard;
    private readonly AudioRecorderService _audio;
    private readonly SpeechToTextService _speech;
    private readonly ObservableCollection<ContextItem> _context = [];
    private bool _collapsed;
    private bool _stoppingVoice;
    private double _expandedWidth = 360;
    private double _expandedHeight = 430;
    private IntPtr _lastExternalWindow;
    private IntPtr _windowHandle;
    private bool _loadingProject = true;

    public MainWindow(
        SettingsService settings,
        ProjectService projects,
        RequestService requests,
        ScreenCaptureService capture,
        ClipboardContextService clipboard,
        AudioRecorderService audio,
        SpeechToTextService speech)
    {
        _settings = settings;
        _projects = projects;
        _requests = requests;
        _capture = capture;
        _clipboard = clipboard;
        _audio = audio;
        _speech = speech;

        InitializeComponent();
        ContextItemsControl.ItemsSource = _context;
        ProjectCombo.ItemsSource = _projects.Projects;
        ProjectCombo.SelectedItem = _projects.ActiveProject;
        CurrentTaskBox.Text = _projects.ActiveProject.CurrentTask;
        _loadingProject = false;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += (_, _) => ApplyClickThrough();
        Deactivated += OnDeactivated;
        Closing += OnClosing;
    }

    public event Action? SettingsRequested;
    public bool AllowClose { get; set; }

    public void ApplySettings()
    {
        Opacity = _settings.Current.Opacity;
        Topmost = _settings.Current.AlwaysOnTop;
        ApplyClickThrough();
    }

    public void ToggleVisible()
    {
        if (_collapsed)
        {
            SetCollapsed(false);
            ShowAndActivate();
            return;
        }

        if (IsVisible && IsActive)
        {
            SaveWindowState();
            Hide();
            return;
        }

        ShowAndActivate();
    }

    public void SaveWindowState()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Current.Window.Left = Left;
            _settings.Current.Window.Top = Top;
            _settings.Current.Window.Width = _collapsed ? _expandedWidth : ActualWidth;
            _settings.Current.Window.Height = _collapsed ? _expandedHeight : ActualHeight;
            _settings.Current.IsCollapsed = _collapsed;
        }

        _settings.Save();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        ApplySettings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();
        if (_settings.Current.IsCollapsed) SetCollapsed(true);
        else MinHeight = 320;
    }

    private void RestoreWindowState()
    {
        var placement = _settings.Current.Window;
        _expandedWidth = Math.Max(MinWidth, placement.Width);
        _expandedHeight = Math.Max(300, placement.Height);
        Width = _expandedWidth;
        Height = _expandedHeight;
        if (!double.IsNaN(placement.Left)) Left = placement.Left;
        if (!double.IsNaN(placement.Top)) Top = placement.Top;
        WindowStyleService.EnsureVisible(this);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != _windowHandle)
            {
                _lastExternalWindow = foreground;
            }

            ApplyClickThrough();
        }, DispatcherPriority.ContextIdle);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowState();
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void ApplyClickThrough()
    {
        var enabled = _settings.Current.ClickThrough switch
        {
            ClickThroughMode.WhenCollapsed => _collapsed,
            ClickThroughMode.WhenInactive => !IsActive,
            _ => false
        };
        WindowStyleService.SetClickThrough(this, enabled);
    }

    private void ShowAndActivate()
    {
        WindowStyleService.SetClickThrough(this, false);
        ShowActivated = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        PromptBox.Focus();
    }

    private void ShowPassive()
    {
        if (IsVisible) return;
        ShowActivated = false;
        Show();
        ShowActivated = true;
    }

    private void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;
        if (collapsed)
        {
            _expandedWidth = ActualWidth;
            _expandedHeight = Math.Max(300, ActualHeight);
            ExpandedContent.Visibility = Visibility.Collapsed;
            ResizeMode = ResizeMode.NoResize;
            MinWidth = 240;
            MinHeight = 48;
            Width = 250;
            Height = 48;
        }
        else
        {
            ExpandedContent.Visibility = Visibility.Visible;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            MinWidth = 310;
            MinHeight = 320;
            Width = _expandedWidth;
            Height = _expandedHeight;
        }

        _settings.Current.IsCollapsed = collapsed;
        ApplyClickThrough();
    }

    private void SetError(string message) => StatusText.Text = $"Error: {message}";

    private void RemoveMissingContextFiles()
    {
        foreach (var item in _context.Where(item => item.FilePath is not null && !File.Exists(item.FilePath)).ToArray())
        {
            _context.Remove(item);
        }
    }

    private void TitleBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not Button &&
            FindParent<ComboBox>(e.OriginalSource as DependencyObject) is null)
        {
            DragMove();
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent) return parent;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void Collapse_OnClick(object sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);
    private void Settings_OnClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private async void CurrentMonitor_OnClick(object sender, RoutedEventArgs e) => await CaptureCurrentMonitorAsync();
    private async void Desktop_OnClick(object sender, RoutedEventArgs e) => await CaptureDesktopAsync();
    private async void AllMonitors_OnClick(object sender, RoutedEventArgs e) => await CaptureAllMonitorsAsync();
    private async void ActiveWindow_OnClick(object sender, RoutedEventArgs e) => await CaptureActiveWindowAsync();
    private async void Region_OnClick(object sender, RoutedEventArgs e) => await CaptureRegionAsync();

    private void Clipboard_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = _clipboard.Read();
            RemoveMissingContextFiles();
            _context.Add(item);
            StatusText.Text = "Clipboard attached";
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void RemoveContext_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: Guid id })
        {
            var item = _context.FirstOrDefault(candidate => candidate.Id == id);
            if (item is not null) _context.Remove(item);
        }
    }

    private void ProjectCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProject) return;
        if (ProjectCombo.SelectedItem is not LinkProject project) return;
        _projects.ActiveProject.CurrentTask = CurrentTaskBox.Text.Trim();
        _projects.SetActive(project);
        CurrentTaskBox.Text = project.CurrentTask;
        _projects.Save();
    }

    private void AddProject_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateProjectWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var project = _projects.Create(dialog.ProjectName);
        _loadingProject = true;
        ProjectCombo.Items.Refresh();
        ProjectCombo.SelectedItem = project;
        CurrentTaskBox.Text = project.CurrentTask;
        _loadingProject = false;
        _projects.SetActive(project);
        CurrentTaskBox.Focus();
    }

    private void CurrentTaskBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        _projects.ActiveProject.CurrentTask = CurrentTaskBox.Text.Trim();
        _projects.Save();
    }

    private void CurrentTaskBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        CurrentTaskPlaceholder.Visibility = string.IsNullOrEmpty(CurrentTaskBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void PromptBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        PromptPlaceholder.Visibility = string.IsNullOrEmpty(PromptBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void Send_OnClick(object sender, RoutedEventArgs e)
    {
        RemoveMissingContextFiles();
        if (string.IsNullOrWhiteSpace(PromptBox.Text) && _context.Count == 0)
        {
            SetError("Add text or context before sending.");
            return;
        }

        _projects.ActiveProject.CurrentTask = CurrentTaskBox.Text.Trim();
        _projects.Save();
        var json = _requests.BuildAndLog(_projects.ActiveProject, PromptBox.Text, _context);
        StatusText.Text = "Request built and saved locally";
        new RequestPreviewWindow(json) { Owner = this }.ShowDialog();
    }
}
