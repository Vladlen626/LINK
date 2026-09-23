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
    private readonly CurrentApplicationService _currentApplication;
    private readonly ScreenshotHistoryService _history;
    private readonly ClipboardContextService _clipboard;
    private readonly IAudioRecorder _audio;
    private readonly ISpeechToText _speech;
    private readonly ObservableCollection<ContextItem> _context = [];
    private bool _collapsed;
    private VoiceState _voiceState;
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
        CurrentApplicationService currentApplication,
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
        _currentApplication = currentApplication;
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
        UpdateEffortButton();

        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
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

        // With Windows transparency effects off the glass turns solid, like the rest of the system.
        var transparent = ThemeService.IsTransparencyEnabled;
        var blurRequested = transparent && _settings.Current.BlurBackground;
        var blurred = WindowStyleService.EnableGlass(this, blurRequested);
        if (FindResource("PanelBrush") is Brush source)
        {
            // Glass setting 0.2..1 maps to a 25..95% tint; unblurred glass needs more tint to keep text readable.
            var tint = source.Clone();
            var opacity = 0.25 + (_settings.Current.Opacity - 0.2) * 0.875;
            tint.Opacity = !transparent ? 1 : blurRequested && !blurred ? Math.Max(opacity, 0.85) : opacity;
            ShellBorder.Background = tint;
        }

        UpdateAutoScreenIndicator();
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

    public void ShowNonBlockingWarning(string message) => SetStatus(message, "Attention");

    public void SaveWindowState()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Current.Window.Left = double.IsFinite(Left) ? Left : null;
            _settings.Current.Window.Top = double.IsFinite(Top) ? Top : null;
            var width = _collapsed ? _expandedWidth : ActualWidth;
            if (double.IsFinite(width) && width > 0) _settings.Current.Window.Width = width;
            if (double.IsFinite(ActualHeight) && ActualHeight > 0) _settings.Current.Window.Height = ActualHeight;
            _settings.Current.IsCollapsed = _collapsed;
        }

        _settings.Save();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_windowHandle)?.AddHook(WindowProcedure);
        TrackExternalForegroundWindow();
        ApplySettings();
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmExitSizeMove && !_collapsed)
        {
            // WPF switches SizeToContent to Manual as soon as a border is dragged.
            // Only the width is user-sized; the height keeps following the content.
            SizeToContent = SizeToContent.Height;
            _expandedWidth = Math.Clamp(ActualWidth, WindowPlacement.MinWidth, WindowPlacement.MaxWidth);
            SaveWindowState();
        }

        return IntPtr.Zero;
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
        _expandedWidth = Math.Clamp(placement.Width, WindowPlacement.MinWidth, WindowPlacement.MaxWidth);
        MinWidth = WindowPlacement.MinWidth;
        MaxWidth = WindowPlacement.MaxWidth;
        Width = _expandedWidth;
        if (placement.Left is { } left) Left = left;
        if (placement.Top is { } top) Top = top;
        WindowStyleService.EnsureVisible(this);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TrackExternalForegroundWindow();
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
        TrackExternalForegroundWindow();
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
            _expandedWidth = Math.Clamp(ActualWidth, WindowPlacement.MinWidth, WindowPlacement.MaxWidth);
            ExpandedContent.Visibility = Visibility.Collapsed;
            ExpandedHeader.Visibility = Visibility.Collapsed;
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
            MaxHeight = 620;
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
            MaxHeight = 620;
            Width = _expandedWidth;
            ClearValue(HeightProperty);
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanResize;
            MinWidth = WindowPlacement.MinWidth;

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
        ScreenshotPreview.ImageSource = null;
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

        if (e.ButtonState != MouseButtonState.Pressed) return;

        e.Handled = true;
        var handle = _windowHandle != IntPtr.Zero ? _windowHandle : new WindowInteropHelper(this).EnsureHandle();
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(handle, NativeMethods.WmNcLButtonDown, new IntPtr(NativeMethods.HtCaption), IntPtr.Zero);
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
        if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "AutoActiveWindow")) is { } autoItem)
        {
            autoItem.IsChecked = _settings.Current.AutoCaptureActiveWindowOnSend;
        }
        menu.PlacementTarget = ScreenButton;
        menu.IsOpen = true;
    }

    private async void CurrentMonitor_OnClick(object sender, RoutedEventArgs e) => await CaptureCurrentMonitorAsync();
    private async void Desktop_OnClick(object sender, RoutedEventArgs e) => await CaptureDesktopAsync();
    private async void AllMonitors_OnClick(object sender, RoutedEventArgs e) => await CaptureAllMonitorsAsync();
    private async void ActiveWindow_OnClick(object sender, RoutedEventArgs e) => await CaptureActiveWindowAsync();
    private async void Region_OnClick(object sender, RoutedEventArgs e) => await CaptureRegionAsync();

    private void AutoActiveWindow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        _settings.Current.AutoCaptureActiveWindowOnSend = item.IsChecked;
        _settings.Save();
        UpdateAutoScreenIndicator();
        SetStatus(item.IsChecked ? "Auto screen enabled" : "Auto screen disabled");
    }

    private void UpdateAutoScreenIndicator()
    {
        if (!IsInitialized) return;
        var enabled = _settings.Current.AutoCaptureActiveWindowOnSend;
        AutoScreenIndicator.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ScreenButton.ToolTip = enabled
            ? "Attach screen context · auto active window is on"
            : "Attach screen context";
    }

    private void TrackExternalForegroundWindow()
    {
        var foreground = _currentApplication.GetExternalForegroundWindow();
        if (foreground != IntPtr.Zero) _lastExternalWindow = foreground;
    }

    private void Clipboard_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        try
        {
            var item = _clipboard.Read();
            RemoveMissingContextFiles();
            foreach (var previous in _context.Where(context => context.Type == ContextItemType.Clipboard).ToArray())
            {
                ReleaseContext(previous);
                _context.Remove(previous);
            }
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
            ScreenshotPreview.ImageSource = null;
            PreviewPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        // A full-resolution 4K capture costs ~33 MB; the preview strip needs a fraction of that.
        image.DecodePixelWidth = 640;
        image.UriSource = new Uri(item.FilePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        _previewContextId = item.Id;
        ScreenshotPreview.ImageSource = image;
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

    private void PromptBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends, Shift+Enter keeps the TextBox newline. IME composition arrives as ImeProcessed.
        if (e.Key != Key.Return || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        // While a request runs the send button means Stop; Enter must never cancel.
        if (!_sending && !e.IsRepeat) Send_OnClick(sender, e);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_sending) return;
        e.Handled = true;
        CancelSend();
    }

}
