namespace Link.Desktop.Models;

/// <summary>
/// What the user gave for one message, fixed when it went out. Every attempt at the message is sent from it, so an
/// answer can be asked for again another way without typing, dictating or capturing anything again.
/// </summary>
public sealed record InputSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public string CurrentTask { get; init; } = string.Empty;

    /// <summary>The prompt, typed or dictated.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The context stack as it was sent, the project state included.</summary>
    public IReadOnlyList<ContextItem> Context { get; init; } = [];

    /// <summary>The app the user worked in; null when Link itself was the target.</summary>
    public CurrentApplicationContext? WorkTarget { get; init; }

    /// <summary>The work target's window, for a new screenshot of it; zero when there is none.</summary>
    public IntPtr WorkWindow { get; init; }

    /// <summary>The screenshot of the work window that Auto active window took for this message.</summary>
    public ContextItem? Screen { get; init; }
}
