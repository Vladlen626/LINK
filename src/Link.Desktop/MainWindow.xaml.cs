using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private readonly ScreenshotHistoryService _history;
    private readonly ClipboardContextService _clipboard;
    private readonly IAudioRecorder _audio;
    private readonly ISpeechToText _speech;
    private readonly ObservableCollection<ContextItem> _context = [];
    private bool _collapsed;
    private bool _stoppingVoice;
    private double _expandedWidth = 340;
    private Guid? _previewContextId;
    private IntPtr _lastExternalWindow;
    private IntPtr _windowHandle;
    private bool _loadingProject = true;

    public MainWindow(
        SettingsService settings,
        ProjectService projects,
        RequestService requests,
        IChatIntegration chat,
        ScreenCaptureService capture,
        ScreenshotHistoryService history,
        ClipboardContextService clipboard,
        IAudioRecorder audio,
        ISpeechToText speech)
    {
        _settings = settings;
        _projects = projects;
        _requests = requests;
        _chat = chat;
        _capture = capture;
        _history = history;
        _clipboard = clipboard;
        _audio = audio;
        _speech = speech;

        InitializeComponent();
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionTimer.Tick += (_, _) => UpdateConnectionState();
        ContextItemsControl.ItemsSource = _context;
        _context.CollectionChanged += Context_OnCollectionChanged;
        LoadProject(_projects.ActiveProject);
        _loadingProject = false;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += (_, _) => ApplyClickThrough();
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _connectionTimer.Stop();
            _sendCancellation?.Cancel();
            _sendCancellation?.Dispose();
        };
    }

    public event Action? SettingsRequested;
    public bool AllowClose { get; set; }

    public void ApplySettings()
    {
        Opacity = 1;
        Topmost = _settings.Current.AlwaysOnTop;

        if (FindResource("PanelBrush") is Brush source)
        {
            var tint = source.Clone();
            tint.Opacity = 0.34 + (_settings.Current.Opacity * 0.38);
            ShellBorder.Background = tint;
        }

        WindowStyleService.EnableBackdrop(this, _settings.Current.Opacity);
        ApplyClickThrough();
    }

    public void ToggleVisible()
    {
        if (IsVisible)
        {
            SaveWindowState();
            Hide();
        }
        else
        {
            ShowAndActivate();
        }
    }

    public void ToggleCollapsed() => SetCollapsed(!_collapsed);

    public void SaveWindowState()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Current.Window.Left = Left;
            _settings.Current.Window.Top = Top;
            _settings.Current.Window.Width = _collapsed ? _expandedWidth : ActualWidth;
            _settings.Current.Window.Height = ActualHeight;
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
        UpdateConnectionState();
        _connectionTimer.Start();
    }

    private void RestoreWindowState()
    {
        var placement = _settings.Current.Window;
        _expandedWidth = Math.Clamp(placement.Width, 320, 400);
        Width = _expandedWidth;
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
            return;
        }

        _sendCancellation?.Cancel();
        ClearContext(prune: true);
    }

    private void ApplyClickThrough()
    {
        // Inactive click-through made the window impossible to recover with a mouse.
        // The explicit power-user behavior is now limited to the compact capsule.
        var enabled = _settings.Current.ClickThrough == ClickThroughMode.WhenCollapsed && _collapsed;
        WindowStyleService.SetClickThrough(this, enabled);
    }

    private void ShowAndActivate()
    {
        WindowStyleService.SetClickThrough(this, false);
        ShowActivated = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (!_collapsed) PromptBox.Focus();
        ApplyClickThrough();
    }

    private void ShowPassive()
    {
        if (IsVisible) return;
        ShowActivated = false;
        Show();
        ShowActivated = true;
        ApplyClickThrough();
    }

    private void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;

        if (collapsed)
        {
            _expandedWidth = Math.Clamp(ActualWidth, 320, 400);
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedHeader.Visibility = Visibility.Collapsed;
            CollapsedHeader.Visibility = Visibility.Visible;
            HeaderRow.Height = new GridLength(46);
            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.NoResize;

            MinWidth = 0;
            MaxWidth = 400;
            Width = 180;
            MinWidth = 180;
            MaxWidth = 180;
            MinHeight = 0;
            MaxHeight = 620;
            Height = 46;
            MinHeight = 46;
            MaxHeight = 46;
            FadeIn(CollapsedHeader);
        }
        else
        {
            MinWidth = 0;
            MaxWidth = 400;
            MinHeight = 0;
            MaxHeight = 620;
            Width = _expandedWidth;
            ClearValue(HeightProperty);
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = 320;

            ExpandedContent.Visibility = Visibility.Visible;
            ExpandedHeader.Visibility = Visibility.Visible;
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

    private void SetStatus(string message, string? collapsedStatus = null)
    {
        StatusText.Text = message;
        CollapsedStatusText.Text = collapsedStatus ?? (message.StartsWith("Error", StringComparison.Ordinal) ? "Attention" : "Ready");
    }

    private void SetError(string message) => SetStatus($"Error: {message}", "Attention");

    private void Context_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ContextItemsControl.Visibility = _context.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    private void AddContext(ContextItem item)
    {
        if (item.FilePath is not null) _history.Pin(item.FilePath);
        _context.Add(item);
    }

    private void ReleaseContext(ContextItem item)
    {
        if (item.FilePath is not null) _history.Unpin(item.FilePath);
    }

    private void ClearContext(bool prune)
    {
        foreach (var item in _context) ReleaseContext(item);
        _context.Clear();
        _previewContextId = null;
        ScreenshotPreview.Source = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
        if (prune) _history.Prune();
    }

    private void RemoveMissingContextFiles()
    {
        foreach (var item in _context.Where(item => item.FilePath is not null && !File.Exists(item.FilePath)).ToArray())
        {
            ReleaseContext(item);
            _context.Remove(item);
        }
    }

    private void TitleBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindParent<ButtonBase>(source) is not null || FindParent<ComboBox>(source) is not null || FindParent<TextBox>(source) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            SetCollapsed(!_collapsed);
            e.Handled = true;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The mouse may have been released between the preview event and DragMove.
            }
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void LoadProject(LinkProject project)
    {
        ProjectNameText.Text = project.Name;
        CurrentTaskBox.Text = project.CurrentTask;
        UpdateConnectionState();
    }

    private void SwitchProject(LinkProject project)
    {
        if (_loadingProject || project.Id == _projects.ActiveProject.Id) return;
        _projects.ActiveProject.CurrentTask = CurrentTaskBox.Text.Trim();
        _projects.SetActive(project);
        LoadProject(project);
        _projects.Save();
    }

    private void ProjectMenu_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProjectMenuButton, Placement = PlacementMode.Bottom };
        foreach (var project in _projects.Projects)
        {
            var item = new MenuItem
            {
                Header = project.Name,
                IsCheckable = true,
                IsChecked = project.Id == _projects.ActiveProject.Id,
                StaysOpenOnClick = false
            };
            item.Click += (_, _) => SwitchProject(project);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "Create project…" };
        create.Click += (_, _) => CreateProject();
        menu.Items.Add(create);
        var rename = new MenuItem { Header = "Rename current…" };
        rename.Click += (_, _) => RenameCurrentProject();
        menu.Items.Add(rename);
        var delete = new MenuItem { Header = "Remove current…", IsEnabled = _projects.Projects.Count > 1 };
        delete.Click += (_, _) => RemoveCurrentProject();
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        var linkConversation = new MenuItem { Header = "Link ChatGPT conversation…" };
        linkConversation.Click += (_, _) => LinkConversation();
        menu.Items.Add(linkConversation);
        var unlinkConversation = new MenuItem
        {
            Header = "Unlink conversation",
            IsEnabled = _projects.ActiveProject.LinkedConversation is not null
        };
        unlinkConversation.Click += (_, _) => UnlinkConversation();
        menu.Items.Add(unlinkConversation);
        menu.IsOpen = true;
    }

    private void CreateProject()
    {
        var dialog = new CreateProjectWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var project = _projects.Create(dialog.ProjectName);
        LoadProject(project);
        CurrentTaskBox.Focus();
    }

    private void RenameCurrentProject()
    {
        var dialog = new CreateProjectWindow(_projects.ActiveProject.Name, isRename: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _projects.Rename(_projects.ActiveProject, dialog.ProjectName);
        ProjectNameText.Text = _projects.ActiveProject.Name;
    }

    private void RemoveCurrentProject()
    {
        if (_projects.Projects.Count <= 1) return;
        var project = _projects.ActiveProject;
        var result = MessageBox.Show(
            $"Remove ‘{project.Name}’ from Link? Its local data folder will be kept.",
            "Remove project",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        var next = _projects.Remove(project);
        LoadProject(next);
        SetStatus("Project removed; local files were kept");
    }

    private void Collapse_OnClick(object sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);
    private void Settings_OnClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void Screen_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("ScreenMenu");
        menu.PlacementTarget = ScreenButton;
        menu.IsOpen = true;
    }

    private async void CurrentMonitor_OnClick(object sender, RoutedEventArgs e) => await CaptureCurrentMonitorAsync();
    private async void Desktop_OnClick(object sender, RoutedEventArgs e) => await CaptureDesktopAsync();
    private async void AllMonitors_OnClick(object sender, RoutedEventArgs e) => await CaptureAllMonitorsAsync();
    private async void ActiveWindow_OnClick(object sender, RoutedEventArgs e) => await CaptureActiveWindowAsync();
    private async void Region_OnClick(object sender, RoutedEventArgs e) => await CaptureRegionAsync();

    private void Clipboard_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        try
        {
            var item = _clipboard.Read();
            RemoveMissingContextFiles();
            AddContext(item);
            SetStatus("Clipboard attached");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void RemoveContext_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: Guid id }) return;
        var item = _context.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null) return;

        ReleaseContext(item);
        _context.Remove(item);
        if (item.Type == ContextItemType.Screenshot) RefreshScreenshotPreview();
        _history.Prune();
    }

    private void RemovePreview_OnClick(object sender, RoutedEventArgs e)
    {
        if (_previewContextId is not { } id) return;
        var item = _context.FirstOrDefault(candidate => candidate.Id == id);
        if (item is not null)
        {
            ReleaseContext(item);
            _context.Remove(item);
        }
        RefreshScreenshotPreview();
        _history.Prune();
    }

    private void RefreshScreenshotPreview()
    {
        var item = _context.LastOrDefault(candidate => candidate.Type == ContextItemType.Screenshot &&
                                                       candidate.FilePath is not null && File.Exists(candidate.FilePath));
        if (item?.FilePath is null)
        {
            _previewContextId = null;
            ScreenshotPreview.Source = null;
            PreviewPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(item.FilePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        _previewContextId = item.Id;
        ScreenshotPreview.Source = image;
        PreviewLabel.Text = item.Label;
        PreviewPanel.Visibility = Visibility.Visible;
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

}
