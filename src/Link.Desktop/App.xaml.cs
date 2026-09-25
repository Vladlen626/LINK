using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class App : System.Windows.Application
{
    private const long MaxLogBytes = 1024 * 1024;
    private readonly Queue<DateTimeOffset> _recentErrors = new();
    private string? _logPath;
    private Mutex? _singleInstance;
    private SettingsService? _settings;
    private ProjectService? _projects;
    private CurrentApplicationService? _currentApplication;
    private GlobalHotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private AudioRecorderService? _audio;
    private SherpaStreamingSpeechToText? _streamingSpeech;
    private TextToSpeechService? _textToSpeech;
    private BrowserChatIntegration? _chat;
    private MainWindow? _mainWindow;
    private bool _started;
    private bool _disposed;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstance = new Mutex(true, @"Local\LinkDesktopCompanion", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Link is already running. Use the tray icon or global show/hide hotkey.", "Link");
            Shutdown();
            return;
        }

        var paths = new StoragePaths();
        _logPath = Path.Combine(paths.Root, "link.log");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };
        _settings = new SettingsService(paths);
        ThemeService.Apply(this, _settings.Current);
        _projects = new ProjectService(paths, _settings);
        var history = new ScreenshotHistoryService(paths, _settings);
        var capture = new ScreenCaptureService(history);
        _currentApplication = new CurrentApplicationService();
        var clipboard = new ClipboardContextService(history);
        var requests = new RequestService(paths);
        _chat = new BrowserChatIntegration();
        _audio = new AudioRecorderService(paths);
        var speechModels = new SpeechModelManager(paths);
        _streamingSpeech = new SherpaStreamingSpeechToText(speechModels);
        _textToSpeech = new TextToSpeechService();
        var voice = new VoiceSession(
            _settings.Current,
            _audio,
            _streamingSpeech,
            new SpeechToTextService(speechModels),
            _textToSpeech,
            TimeProvider.System);

        _mainWindow = new MainWindow(
            _settings,
            _projects,
            requests,
            _chat,
            capture,
            _currentApplication,
            history,
            clipboard,
            voice);
        MainWindow = _mainWindow;
        _mainWindow.SettingsRequested += ShowSettings;
        _mainWindow.NotificationRequested += ShowNotification;
        _mainWindow.Show();

        _hotkeys = new GlobalHotkeyService(_mainWindow, Dispatcher);
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.PushToTalkStarted += () =>
        {
            _currentApplication.LatchForeground();
            _mainWindow.StartPushToTalk();
        };
        _hotkeys.PushToTalkEnded += async () => await _mainWindow.StopPushToTalkAsync();
        TryStartHotkeys();

        _tray = new TrayIconService(
            () => Dispatcher.Invoke(_mainWindow.ToggleVisible),
            () => Dispatcher.Invoke(ShowSettings),
            () => Dispatcher.Invoke(ExitApplication));
        _started = true;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (_mainWindow is not null) _mainWindow.AllowClose = true;
        SaveState();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeServices();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        if (!_started)
        {
            // Without its window and tray a half-started Link would only hold the single-instance lock.
            e.Handled = true;
            MessageBox.Show($"Link could not start: {e.Exception.Message}\n\nDetails: {_logPath}", "Link",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // A tray companion that silently vanishes is worse than one that reports the failure and keeps going.
        // An error that repeats in a burst (e.g. during layout) would loop forever, so that one still ends the app.
        var now = DateTimeOffset.Now;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Peek() < now.AddSeconds(-10)) _recentErrors.Dequeue();
        if (_recentErrors.Count > 5) return;

        e.Handled = true;
        _mainWindow?.ShowNonBlockingWarning($"Unexpected error: {e.Exception.Message} Details are in link.log.");
    }

    private void LogError(Exception exception)
    {
        if (_logPath is null) return;
        try
        {
            var log = new FileInfo(_logPath);
            if (log.Exists && log.Length > MaxLogBytes) log.Delete();
            File.AppendAllText(_logPath, $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never raise a second failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Logging must never raise a second failure.
        }
    }

    private async void OnHotkeyPressed(HotkeyAction action)
    {
        if (_mainWindow is null) return;
        // A global hotkey is an explicit invocation: the app in front is what the user is working on.
        _currentApplication?.LatchForeground();
        switch (action)
        {
            case HotkeyAction.ShowHide:
                _mainWindow.ToggleVisible();
                break;
            case HotkeyAction.Send:
                await _mainWindow.SendFromHotkeyAsync();
                break;
            case HotkeyAction.CaptureDesktop:
                await _mainWindow.CaptureDesktopAsync();
                break;
            case HotkeyAction.CaptureActiveWindow:
                await _mainWindow.CaptureActiveWindowAsync();
                break;
            case HotkeyAction.CaptureRegion:
                await _mainWindow.CaptureRegionAsync();
                break;
        }
    }

    private void ShowNotification(string title, string message, Guid projectId) =>
        _tray?.ShowNotification(title, message, () => Dispatcher.Invoke(() => _mainWindow?.ShowProject(projectId)));

    private void ShowSettings()
    {
        if (_settings is null || _mainWindow is null || _hotkeys is null || _audio is null || _textToSpeech is null) return;
        var previousHotkeys = _settings.Current.Hotkeys.Clone();
        var hotkeysRestarted = false;
        _hotkeys.Stop();
        try
        {
            var window = new SettingsWindow(_settings, _audio, _textToSpeech);
            if (_mainWindow.IsVisible) window.Owner = _mainWindow;
            if (window.ShowDialog() == true)
            {
                _mainWindow.ApplySettings();
                try
                {
                    var failures = _hotkeys.Start(_settings.Current.Hotkeys);
                    hotkeysRestarted = true;
                    if (failures.Count > 0)
                    {
                        _mainWindow.ShowNonBlockingWarning($"Hotkey unavailable: {string.Join("; ", failures)}.");
                    }
                }
                catch (Exception exception)
                {
                    _settings.Current.Hotkeys = previousHotkeys;
                    _settings.Save();
                    try
                    {
                        _hotkeys.Start(previousHotkeys);
                        hotkeysRestarted = true;
                    }
                    catch (Exception)
                    {
                        // The original binding may have been claimed while Settings was open.
                    }
                    MessageBox.Show(
                        $"The new shortcut binding could not be registered and was reverted:\n{exception.Message}",
                        "Link",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            else
            {
                var failures = _hotkeys.Start(previousHotkeys);
                hotkeysRestarted = true;
                if (failures.Count > 0)
                {
                    _mainWindow.ShowNonBlockingWarning($"Hotkey unavailable: {string.Join("; ", failures)}.");
                }
            }
        }
        finally
        {
            if (!hotkeysRestarted)
            {
                try
                {
                    _hotkeys.Start(previousHotkeys);
                }
                catch (Exception exception)
                {
                    _mainWindow.ShowNonBlockingWarning($"Global hotkeys could not be restored: {exception.Message}");
                }
            }
        }
    }

    private void TryStartHotkeys()
    {
        if (_hotkeys is null || _settings is null) return;
        try
        {
            var failures = _hotkeys.Start(_settings.Current.Hotkeys);
            if (failures.Count > 0)
            {
                _mainWindow?.ShowNonBlockingWarning($"Hotkey unavailable: {string.Join("; ", failures)}.");
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Global hotkeys are unavailable:\n{exception.Message}", "Link", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            SaveState();
            _mainWindow.Close();
        }

        DisposeServices();
        Shutdown();
    }

    private void SaveState()
    {
        try
        {
            _mainWindow?.SaveWindowState();
            _projects?.Save();
        }
        catch (Exception)
        {
            // Windows shutdown must not be blocked by a storage or registry error.
        }
    }

    private void DisposeServices()
    {
        if (_disposed) return;
        _disposed = true;
        _audio?.Cancel();
        _hotkeys?.Dispose();
        _currentApplication?.Dispose();
        _audio?.Dispose();
        _streamingSpeech?.Dispose();
        _textToSpeech?.Dispose();
        _chat?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
    }
}
