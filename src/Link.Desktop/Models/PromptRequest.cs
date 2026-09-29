namespace Link.Desktop.Models;

public sealed class PromptRequest
{
    // 4: the route. 5: the conversation hint.
    public int SchemaVersion { get; init; } = 5;
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public RequestRoute Route { get; init; } = RequestRoute.Chat;
    public string CurrentTask { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public ResponseEffort Effort { get; init; } = ResponseEffort.Normal;

    /// <summary>Asked in conversation mode, where the answer is read aloud: Chat asks for a short spoken start.</summary>
    public bool Conversation { get; init; }

    public CurrentApplicationContext? CurrentApplication { get; init; }
    public IReadOnlyList<ContextItem> Context { get; init; } = [];
}

/// <summary>How a message goes to the chat.</summary>
public enum RequestRoute
{
    /// <summary>With what Link adds: the project, its task and state, the work window and its screenshot, a depth hint.</summary>
    Chat,

    /// <summary>Only the text and the context the user attached, to see what Link's additions are worth.</summary>
    Direct
}

/// <summary>How much time and length the assistant should spend on an answer.</summary>
public enum ResponseEffort
{
    Normal,
    Fast,
    Deep
}
