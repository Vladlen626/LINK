using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
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
    private readonly VoiceSession _voice;
    private readonly ObservableCollection<ContextItem> _context = [];
    private bool _collapsed;
    private double _expandedWidth = 340;
    private Guid? _previewContextId;
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
        VoiceSession voice)
    {
        _settings = settings;
        _projects = projects;
        _requests = requests;
        _chat = chat;
        _capture = capture;
        _currentApplication = currentApplication;
        _history = history;
        _clipboard = clipboard;
        _voice = voice;

        InitializeComponent();
        _voice.PhaseChanged += OnVoicePhaseChanged;
        _resizeBorder = WindowChrome.GetWindowChrome(this).ResizeBorderThickness;
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
        Activated += OnActivated;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _connectionTimer.Stop();
            _sendCancellation?.Cancel();
            _sendCancellation?.Dispose();
        };
    }

    public event Action? SettingsRequested;

    /// <summary>A Windows notification is due: title, message and the project it opens.</summary>
    public event Action<string, string, Guid>? NotificationRequested;

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

    /// <summary>Brings Link up on a project's answer, e.g. when its notification is clicked.</summary>
    public void ShowProject(Guid projectId)
    {
        if (!_sending && _projects.Projects.FirstOrDefault(project => project.Id == projectId) is { } project)
        {
            if (project.Id != _projects.ActiveProject.Id) SwitchProject(project);
            else if (ResponsePanel.Visibility != Visibility.Visible) ShowProjectResponse(project);
        }

        SetCollapsed(false);
        ShowAndActivate();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_windowHandle)?.AddHook(WindowProcedure);
        ApplySettings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();
        if (_settings.Current.IsCollapsed) SetCollapsed(true);
        UpdateConnectionState();
        _connectionTimer.Start();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // Coming back from an app the user worked in makes it the target; watching the linked chat does not.
        _currentApplication.LatchRecent(_chat.IsConversationWindow);
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

    private void ShowAndActivate()
    {
        ShowActivated = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (!_collapsed) PromptBox.Focus();
    }

    private void ShowPassive()
    {
        if (IsVisible) return;
        ShowActivated = false;
        Show();
        ShowActivated = true;
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
        ShowProjectResponse(project);
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

    private void PromptBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(PromptBox.Text);
        PromptPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // A cleared prompt no longer holds the dictated words, so its answer is not read aloud.
        if (empty) _voice.ForgetVoicePrompt();
    }

    private async void PromptBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends, Shift+Enter keeps the TextBox newline. IME composition arrives as ImeProcessed.
        if (e.Key != Key.Return || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        // The send button may mean Stop right now; Enter only ever sends.
        if (!_sending && !e.IsRepeat) await SendOrQueueAsync();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // Like the Stop orb: silence Link first, then stop the request.
        if (_voice.Phase == VoicePhase.Speaking) _voice.StopSpeaking();
        else if (_sending) CancelSend();
        else return;
        e.Handled = true;
    }

}
