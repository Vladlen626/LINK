namespace Link.Desktop.Models;

public sealed class PromptRequest
{
    // 4: the route. 5: the conversation hint. 6: the task is the project note's first line, in the context.
    // 7: no route or effort. 8: no conversation hint.
    public int SchemaVersion { get; init; } = 8;
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public string Text { get; init; } = string.Empty;

    public CurrentApplicationContext? CurrentApplication { get; init; }
    public IReadOnlyList<ContextItem> Context { get; init; } = [];
}

/// <summary>Where Send goes. Saved as a number: 1 was "only my text", read as Chat now.</summary>
public enum RequestRoute
{
    /// <summary>To ChatGPT with what Link adds: the project and its note, the work window and its screenshot.</summary>
    Chat = 0,

    /// <summary>Not to the chat: a CLI agent works in the project's workspace, after a confirmation card.</summary>
    Agent = 2
}
