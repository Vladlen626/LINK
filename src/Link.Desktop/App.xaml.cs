using System.Threading;
using System.Windows;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private SettingsService? _settings;
    private ProjectService? _projects;
    private GlobalHotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private AudioRecorderService? _audio;
    private MainWindow? _mainWindow;
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
        _settings = new SettingsService(paths);
        _projects = new ProjectService(paths, _settings);
        var history = new ScreenshotHistoryService(paths, _settings);
        var capture = new ScreenCaptureService(history);
        var clipboard = new ClipboardContextService(history);
        var requests = new RequestService(paths);
        _audio = new AudioRecorderService(paths);
        var speech = new SpeechToTextService();

        _mainWindow = new MainWindow(_settings, _projects, requests, capture, clipboard, _audio, speech);
        MainWindow = _mainWindow;
        _mainWindow.SettingsRequested += ShowSettings;

        _hotkeys = new GlobalHotkeyService(Dispatcher);
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.PushToTalkStarted += () => _mainWindow.StartPushToTalk();
        _hotkeys.PushToTalkEnded += async () => await _mainWindow.StopPushToTalkAsync();
        TryStartHotkeys();

        _tray = new TrayIconService(
            () => Dispatcher.Invoke(_mainWindow.ToggleVisible),
            () => Dispatcher.Invoke(ShowSettings),
            () => Dispatcher.Invoke(ExitApplication));

        _mainWindow.Show();
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

    private async void OnHotkeyPressed(HotkeyAction action)
    {
        if (_mainWindow is null) return;
        switch (action)
        {
            case HotkeyAction.ShowHide:
                _mainWindow.ToggleVisible();
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

    private void ShowSettings()
    {
        if (_settings is null || _mainWindow is null || _hotkeys is null) return;
        _hotkeys.IsSuspended = true;
        try
        {
            var window = new SettingsWindow(_settings);
            if (_mainWindow.IsVisible) window.Owner = _mainWindow;
            if (window.ShowDialog() == true)
            {
                _mainWindow.ApplySettings();
                TryStartHotkeys();
            }
        }
        finally
        {
            _hotkeys.IsSuspended = false;
        }
    }

    private void TryStartHotkeys()
    {
        if (_hotkeys is null || _settings is null) return;
        try
        {
            _hotkeys.Start(_settings.Current.Hotkeys);
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
        _audio?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
    }
}
