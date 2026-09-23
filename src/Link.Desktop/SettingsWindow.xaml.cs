using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private TextBox? _capturingHotkey;
    private string? _hotkeyBeforeCapture;

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        var current = settings.Current;
        OpacitySlider.Value = current.Opacity;
        VoiceLanguageCombo.ItemsSource = Enum.GetValues<VoiceLanguage>();
        VoiceLanguageCombo.SelectedItem = current.VoiceLanguage;
        ClickThroughCombo.ItemsSource = new[] { ClickThroughMode.Off, ClickThroughMode.WhenCollapsed };
        ClickThroughCombo.SelectedItem = current.ClickThrough;
        HistorySizeBox.Text = current.ScreenshotHistorySize.ToString();
        AlwaysOnTopBox.IsChecked = current.AlwaysOnTop;
        LaunchWithWindowsBox.IsChecked = current.LaunchWithWindows;
        ShowHideHotkey.Text = current.Hotkeys.ShowHide;
        PushToTalkHotkey.Text = current.Hotkeys.PushToTalk;
        DesktopHotkey.Text = current.Hotkeys.CaptureDesktop;
        WindowHotkey.Text = current.Hotkeys.CaptureActiveWindow;
        RegionHotkey.Text = current.Hotkeys.CaptureRegion;
        VersionText.Text = $"Version {GetInformationalVersion()}";
    }

    private static string GetInformationalVersion()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var buildId = assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];
        return $"{version} · build {buildId}";
    }

    private void HotkeyBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox hotkeyBox) return;
        if (_capturingHotkey != hotkeyBox)
        {
            EndHotkeyCapture(restorePrevious: true);
            _capturingHotkey = hotkeyBox;
            _hotkeyBeforeCapture = hotkeyBox.Text;
            hotkeyBox.Text = "Press shortcut…";
            hotkeyBox.BorderBrush = (Brush)FindResource("AccentBorderBrush");
            hotkeyBox.Background = (Brush)FindResource("ActiveContextBrush");
        }

        hotkeyBox.Focus();
        ErrorText.Text = string.Empty;
        e.Handled = true;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey is null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndHotkeyCapture(restorePrevious: true);
            ErrorText.Text = string.Empty;
            return;
        }

        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var hasModifier = modifiers != ModifierKeys.None;
        var isFunctionKey = key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll or Key.Insert;
        if (!hasModifier && !isFunctionKey)
        {
            ErrorText.Text = "Use Ctrl, Alt, Shift or Win with ordinary keys.";
            return;
        }

        ErrorText.Text = string.Empty;
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        _capturingHotkey.Text = string.Join('+', parts);
        EndHotkeyCapture(restorePrevious: false);
    }

    private void EndHotkeyCapture(bool restorePrevious)
    {
        if (_capturingHotkey is null) return;
        if (restorePrevious && _hotkeyBeforeCapture is not null)
        {
            _capturingHotkey.Text = _hotkeyBeforeCapture;
        }

        _capturingHotkey.ClearValue(BorderBrushProperty);
        _capturingHotkey.ClearValue(BackgroundProperty);
        _capturingHotkey = null;
        _hotkeyBeforeCapture = null;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        EndHotkeyCapture(restorePrevious: true);
        if (!int.TryParse(HistorySizeBox.Text, out var historySize) || historySize is < 1 or > 20)
        {
            ErrorText.Text = "Screenshot history must be between 1 and 20.";
            return;
        }

        var hotkeys = new[] { ShowHideHotkey.Text, PushToTalkHotkey.Text, DesktopHotkey.Text, WindowHotkey.Text, RegionHotkey.Text };
        if (hotkeys.Any(string.IsNullOrWhiteSpace) || hotkeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != hotkeys.Length)
        {
            ErrorText.Text = "Each action must have a unique hotkey.";
            return;
        }

        try
        {
            if (hotkeys.Select(HotkeyGesture.Parse).Any(gesture => !gesture.IsSafeGlobalShortcut))
            {
                ErrorText.Text = "Global shortcuts must include a modifier or use a function key.";
                return;
            }
        }
        catch (FormatException exception)
        {
            ErrorText.Text = exception.Message;
            return;
        }

        var current = _settings.Current;
        current.Opacity = OpacitySlider.Value;
        current.VoiceLanguage = (VoiceLanguage)(VoiceLanguageCombo.SelectedItem ?? VoiceLanguage.Auto);
        current.ClickThrough = (ClickThroughMode)(ClickThroughCombo.SelectedItem ?? ClickThroughMode.WhenCollapsed);
        current.ScreenshotHistorySize = historySize;
        current.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        current.LaunchWithWindows = LaunchWithWindowsBox.IsChecked == true;
        current.Hotkeys.ShowHide = ShowHideHotkey.Text;
        current.Hotkeys.PushToTalk = PushToTalkHotkey.Text;
        current.Hotkeys.CaptureDesktop = DesktopHotkey.Text;
        current.Hotkeys.CaptureActiveWindow = WindowHotkey.Text;
        current.Hotkeys.CaptureRegion = RegionHotkey.Text;

        try
        {
            _settings.Save();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        EndHotkeyCapture(restorePrevious: true);
        DialogResult = false;
    }
}
