namespace Link.Desktop.Models;

public sealed class AppSettings
{
    // Answers are read a little quicker than a voice's normal pace.
    private const double DefaultVoiceReplySpeed = 1.2;
    // Described as warm: the easiest to listen to all day. Any prebuilt voice speaks Russian.
    private const string DefaultGeminiVoice = "Sulafat";

    public int SchemaVersion { get; set; } = 11;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public VoiceLanguage VoiceLanguage { get; set; } = VoiceLanguage.Auto;
    public string? MicrophoneDeviceId { get; set; }
    public string? MicrophoneDeviceName { get; set; }

    /// <summary>Show the words in the prompt while push-to-talk is held; the final transcript replaces them.</summary>
    public bool LiveTranscription { get; set; } = true;

    /// <summary>Read the answer to a dictated prompt aloud.</summary>
    public bool VoiceReplies { get; set; } = true;

    /// <summary>With <see cref="VoiceReplies"/>, answers to typed prompts are read aloud too.</summary>
    public bool ReadTypedAnswers { get; set; }

    /// <summary>The voice that reads answers first; the ones after it in Auto's order stand in when it cannot.</summary>
    public TtsProvider VoiceReplyProvider { get; set; } = TtsProvider.Auto;

    /// <summary>A Gemini prebuilt voice, kept between answers so that Link always sounds the same.</summary>
    public string GeminiVoice { get; set; } = DefaultGeminiVoice;

    /// <summary>The speaker of the local voice model.</summary>
    public int LocalVoice { get; set; }

    public double VoiceReplySpeed { get; set; } = DefaultVoiceReplySpeed;

    /// <summary>Short sounds for listening, sent, an answer ready, a question for the user and an error.</summary>
    public bool SoundCues { get; set; } = true;

    /// <summary>Where Send sends: to ChatGPT, or to the project's agent through its task card.</summary>
    public RequestRoute Route { get; set; } = RequestRoute.Chat;
    public int ScreenshotHistorySize { get; set; } = 5;
    public bool LaunchWithWindows { get; set; }

    /// <summary>Keeps the last request, with its prompt, file paths and window title, in last-request.json.</summary>
    public bool Diagnostics { get; set; }
    public bool AutoCaptureActiveWindowOnSend { get; set; }

    /// <summary>A short outline around a window Link has just captured, so that the user sees which window went.</summary>
    public bool OutlineCapturedWindow { get; set; } = true;
    public Guid? ActiveProjectId { get; set; }
    public StudioSettings Studio { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public AgentSettings Agent { get; set; } = new();
    public RemoteSettings Remote { get; set; } = new();

    public void Normalize()
    {
        Agent ??= new AgentSettings();
        Remote ??= new RemoteSettings();
        Studio ??= new StudioSettings();
        Hotkeys ??= new HotkeySettings();
        if (SchemaVersion < 4)
        {
            if (string.Equals(Hotkeys.CaptureRegion, "Ctrl+Alt+R", StringComparison.OrdinalIgnoreCase))
            {
                Hotkeys.CaptureRegion = "Ctrl+Alt+Shift+R";
            }
        }
        // 6: the overlay card's glass tint was reset; the card and its glass are gone.
        // 7: Hide replaced the collapsed capsule. Its IsCollapsed flag is no longer read, so Link opens in full.
        // 8: the Context budget's default went from 550k to 450k; a saved 550k is the old default, not a choice.
        if (SchemaVersion < 8 && Agent.ContextBudget == 550_000) Agent.ContextBudget = 450_000;
        // 9: the Light levels' defaults became the team's cheap workers (Haiku 5.5, Codex's Luna); the old defaults move.
        if (SchemaVersion < 9) Agent.MoveOldLightDefaults();
        // 10: the Context budget's default became Auto, the CLI's own window; a saved 450k is the old default, not a choice.
        if (SchemaVersion < 10 && Agent.ContextBudget == 450_000) Agent.ContextBudget = 0;
        // 11: the default became 250k; a saved Auto is the old default, not a choice.
        if (SchemaVersion < 11 && Agent.ContextBudget == 0) Agent.ContextBudget = AgentSettings.DefaultContextBudget;
        SchemaVersion = 11;
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        ScreenshotHistorySize = Math.Clamp(ScreenshotHistorySize, 1, 20);
        // 1, "only my text", is gone with the other values a newer Link may save.
        if (!Enum.IsDefined(Route)) Route = RequestRoute.Chat;
        if (string.IsNullOrWhiteSpace(MicrophoneDeviceId))
        {
            MicrophoneDeviceId = null;
            MicrophoneDeviceName = null;
        }
        // Also catches the removed Windows voice (3) in older files.
        if (!Enum.IsDefined(VoiceReplyProvider)) VoiceReplyProvider = TtsProvider.Auto;
        GeminiVoice = string.IsNullOrWhiteSpace(GeminiVoice) ? DefaultGeminiVoice : GeminiVoice.Trim();
        LocalVoice = Math.Max(0, LocalVoice);
        VoiceReplySpeed = double.IsFinite(VoiceReplySpeed)
            ? Math.Round(Math.Clamp(VoiceReplySpeed, 0.5, 2.0), 2)
            : DefaultVoiceReplySpeed;
        Studio.Normalize();
        Hotkeys.Normalize();
        Agent.Normalize();
    }
}

/// <summary>
/// The Link window: where it was left, how big, which tab it shows, whether its tasks sidebar and its right column are
/// open and on what.
/// </summary>
public sealed class StudioSettings
{
    public const double MinWidth = 760;
    public const double MinHeight = 480;
    public const double MaxWidth = 8000;
    public const double MaxHeight = 4320;
    public const double DefaultWidth = 1100;
    public const double DefaultHeight = 720;
    public const int DefaultSpendDays = 30;
    // No screen is wider than this: beyond it a place is nonsense.
    private const double FarthestPlace = 100_000;

    /// <summary>Null with <see cref="Top"/>: the window opens centered.</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = DefaultWidth;
    public double Height { get; set; } = DefaultHeight;
    public bool Maximized { get; set; }
    public StudioTab Tab { get; set; } = StudioTab.Chat;
    public bool InspectorOpen { get; set; } = true;
    public bool SidebarOpen { get; set; } = true;

    /// <summary>What the right column shows when it is open.</summary>
    public StudioSide Side { get; set; } = StudioSide.Harness;

    /// <summary>How many days back the Spend history shows, and whether of every project or the shown one.</summary>
    public int SpendDays { get; set; } = DefaultSpendDays;

    public bool SpendAllProjects { get; set; }

    /// <summary>Keeps where the window stands, its normal bounds when it is maximized.</summary>
    public void Remember(double left, double top, double width, double height, bool maximized)
    {
        (Left, Top, Width, Height, Maximized) = (left, top, width, height, maximized);
        Normalize();
    }

    public void Normalize()
    {
        // A place is both numbers or none, and a number beyond any screen is none.
        if (Left is not { } left || Top is not { } top || !IsPlace(left) || !IsPlace(top)) (Left, Top) = (null, null);
        Width = double.IsFinite(Width) && Width > 0 ? Math.Clamp(Width, MinWidth, MaxWidth) : DefaultWidth;
        Height = double.IsFinite(Height) && Height > 0 ? Math.Clamp(Height, MinHeight, MaxHeight) : DefaultHeight;
        SpendDays = SpendDays <= 0 ? DefaultSpendDays : Math.Clamp(SpendDays, 7, Services.UsageHistory.KeptDays);
        if (!Enum.IsDefined(Tab)) Tab = StudioTab.Chat;
        if (!Enum.IsDefined(Side)) Side = StudioSide.Harness;
    }

    private static bool IsPlace(double value) => double.IsFinite(value) && Math.Abs(value) <= FarthestPlace;
}

/// <summary>The Link window's tabs: the chats with ChatGPT, and the Studio of the agents' tasks.</summary>
public enum StudioTab
{
    Chat,
    Studio
}

/// <summary>
/// What the window's right column shows: what the agent really gets, where its tokens went, or the project itself (its
/// note, workspace, map, skills and check).
/// </summary>
public enum StudioSide
{
    Harness,
    Spend,
    Project
}

public sealed class HotkeySettings
{
    public string ShowHide { get; set; } = "Ctrl+Alt+Space";
    public string PushToTalk { get; set; } = "Ctrl+Alt+V";
    public string Send { get; set; } = "Ctrl+Alt+Enter";
    public string CaptureDesktop { get; set; } = "Ctrl+Alt+D";
    public string CaptureActiveWindow { get; set; } = "Ctrl+Alt+W";
    public string CaptureRegion { get; set; } = "Ctrl+Alt+Shift+R";

    public void Normalize()
    {
        ShowHide = DefaultIfEmpty(ShowHide, "Ctrl+Alt+Space");
        PushToTalk = DefaultIfEmpty(PushToTalk, "Ctrl+Alt+V");
        Send = DefaultIfEmpty(Send, "Ctrl+Alt+Enter");
        CaptureDesktop = DefaultIfEmpty(CaptureDesktop, "Ctrl+Alt+D");
        CaptureActiveWindow = DefaultIfEmpty(CaptureActiveWindow, "Ctrl+Alt+W");
        CaptureRegion = DefaultIfEmpty(CaptureRegion, "Ctrl+Alt+Shift+R");
    }

    public HotkeySettings Clone() => new()
    {
        ShowHide = ShowHide,
        PushToTalk = PushToTalk,
        Send = Send,
        CaptureDesktop = CaptureDesktop,
        CaptureActiveWindow = CaptureActiveWindow,
        CaptureRegion = CaptureRegion
    };

    private static string DefaultIfEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum VoiceLanguage
{
    Auto,
    Russian,
    English
}

/// <summary>Who turns replies into speech. Auto: Gemini when it has a key, then the local voice.</summary>
public enum TtsProvider
{
    Auto,
    Gemini,
    Local
}
