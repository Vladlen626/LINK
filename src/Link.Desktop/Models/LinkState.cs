namespace Link.Desktop.Models;

/// <summary>What Link is up to, as its tray icon shows it while Link is hidden.</summary>
public enum LinkState
{
    Idle,

    /// <summary>A request is on its way to the chat, or its answer is arriving.</summary>
    Working,

    /// <summary>Link waits for the user: ChatGPT asks to sign in, or a request failed while Link was hidden.</summary>
    NeedsDecision,

    /// <summary>An answer came while Link was hidden; it stays so until Link is shown again.</summary>
    Done
}
