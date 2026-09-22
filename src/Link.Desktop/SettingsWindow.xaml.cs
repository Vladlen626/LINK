using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;

    public SettingsWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        var current = settings.Current;
        OpacitySlider.Value = current.Opacity;
        VoiceLanguageCombo.ItemsSource = Enum.GetValues<VoiceLanguage>();
        VoiceLanguageCombo.SelectedItem = current.VoiceLanguage;
        ClickThroughCombo.ItemsSource = Enum.GetValues<ClickThroughMode>();
        ClickThroughCombo.SelectedItem = current.ClickThrough;
        HistorySizeBox.Text = current.ScreenshotHistorySize.ToString();
        AlwaysOnTopBox.IsChecked = current.AlwaysOnTop;
        LaunchWithWindowsBox.IsChecked = current.LaunchWithWindows;
        ShowHideHotkey.Text = current.Hotkeys.ShowHide;
        PushToTalkHotkey.Text = current.Hotkeys.PushToTalk;
        DesktopHotkey.Text = current.Hotkeys.CaptureDesktop;
        WindowHotkey.Text = current.Hotkeys.CaptureActiveWindow;
        RegionHotkey.Text = current.Hotkeys.CaptureRegion;
    }

    private void HotkeyBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        ((TextBox)sender).Text = string.Join('+', parts);
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
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

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
