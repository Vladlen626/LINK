namespace Link.Desktop.Models;

public sealed class AppSettings
{
    public double Opacity { get; set; } = 0.94;
    public bool AlwaysOnTop { get; set; } = true;
    public ClickThroughMode ClickThrough { get; set; } = ClickThroughMode.WhenCollapsed;
    public VoiceLanguage VoiceLanguage { get; set; } = VoiceLanguage.Auto;
    public int ScreenshotHistorySize { get; set; } = 5;
    public bool LaunchWithWindows { get; set; }
    public bool IsCollapsed { get; set; }
    public Guid? ActiveProjectId { get; set; }
    public WindowPlacement Window { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();

    public void Normalize()
    {
        Opacity = Math.Clamp(Opacity, 0.45, 1.0);
        ScreenshotHistorySize = Math.Clamp(ScreenshotHistorySize, 1, 20);
        Window ??= new WindowPlacement();
        Hotkeys ??= new HotkeySettings();
        Hotkeys.Normalize();
    }
}

public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 500;
}

public sealed class HotkeySettings
{
    public string ShowHide { get; set; } = "Ctrl+Alt+Space";
    public string PushToTalk { get; set; } = "Ctrl+Alt+V";
    public string CaptureDesktop { get; set; } = "Ctrl+Alt+D";
    public string CaptureActiveWindow { get; set; } = "Ctrl+Alt+W";
    public string CaptureRegion { get; set; } = "Ctrl+Alt+R";

    public void Normalize()
    {
        ShowHide = DefaultIfEmpty(ShowHide, "Ctrl+Alt+Space");
        PushToTalk = DefaultIfEmpty(PushToTalk, "Ctrl+Alt+V");
        CaptureDesktop = DefaultIfEmpty(CaptureDesktop, "Ctrl+Alt+D");
        CaptureActiveWindow = DefaultIfEmpty(CaptureActiveWindow, "Ctrl+Alt+W");
        CaptureRegion = DefaultIfEmpty(CaptureRegion, "Ctrl+Alt+R");
    }

    private static string DefaultIfEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public enum ClickThroughMode
{
    Off,
    WhenCollapsed,
    WhenInactive
}

public enum VoiceLanguage
{
    Auto,
    Russian,
    English
}
