namespace Link.Desktop.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 4;
    public double Opacity { get; set; } = 0.72;
    public bool AlwaysOnTop { get; set; } = true;
    public ClickThroughMode ClickThrough { get; set; } = ClickThroughMode.Off;
    public VoiceLanguage VoiceLanguage { get; set; } = VoiceLanguage.Auto;
    public int ScreenshotHistorySize { get; set; } = 5;
    public bool LaunchWithWindows { get; set; }
    public bool IsCollapsed { get; set; }
    public Guid? ActiveProjectId { get; set; }
    public WindowPlacement Window { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();

    public void Normalize()
    {
        Window ??= new WindowPlacement();
        Hotkeys ??= new HotkeySettings();
        if (SchemaVersion < 2)
        {
            // Earlier builds enabled click-through by default, which made the capsule feel unreachable.
            ClickThrough = ClickThroughMode.Off;
        }
        if (SchemaVersion < 4)
        {
            if (string.Equals(Hotkeys.CaptureRegion, "Ctrl+Alt+R", StringComparison.OrdinalIgnoreCase))
            {
                Hotkeys.CaptureRegion = "Ctrl+Alt+Shift+R";
            }
        }
        SchemaVersion = 4;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.45, 1.0) : 0.72;
        ScreenshotHistorySize = Math.Clamp(ScreenshotHistorySize, 1, 20);
        if (ClickThrough == ClickThroughMode.WhenInactive) ClickThrough = ClickThroughMode.Off;
        Window.Normalize();
        Hotkeys.Normalize();
    }
}

public sealed class WindowPlacement
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 240;

    public void Normalize()
    {
        if (Left is { } left && !double.IsFinite(left)) Left = null;
        if (Top is { } top && !double.IsFinite(top)) Top = null;
        Width = double.IsFinite(Width) && Width > 0 ? Math.Clamp(Width, 320, 400) : 340;
        Height = double.IsFinite(Height) && Height > 0 ? Math.Clamp(Height, 46, 620) : 240;
    }
}

public sealed class HotkeySettings
{
    public string ShowHide { get; set; } = "Ctrl+Alt+Space";
    public string PushToTalk { get; set; } = "Ctrl+Alt+V";
    public string CaptureDesktop { get; set; } = "Ctrl+Alt+D";
    public string CaptureActiveWindow { get; set; } = "Ctrl+Alt+W";
    public string CaptureRegion { get; set; } = "Ctrl+Alt+Shift+R";

    public void Normalize()
    {
        ShowHide = DefaultIfEmpty(ShowHide, "Ctrl+Alt+Space");
        PushToTalk = DefaultIfEmpty(PushToTalk, "Ctrl+Alt+V");
        CaptureDesktop = DefaultIfEmpty(CaptureDesktop, "Ctrl+Alt+D");
        CaptureActiveWindow = DefaultIfEmpty(CaptureActiveWindow, "Ctrl+Alt+W");
        CaptureRegion = DefaultIfEmpty(CaptureRegion, "Ctrl+Alt+Shift+R");
    }

    public HotkeySettings Clone() => new()
    {
        ShowHide = ShowHide,
        PushToTalk = PushToTalk,
        CaptureDesktop = CaptureDesktop,
        CaptureActiveWindow = CaptureActiveWindow,
        CaptureRegion = CaptureRegion
    };

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
