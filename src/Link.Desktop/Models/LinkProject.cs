using System.Text.Json.Serialization;

namespace Link.Desktop.Models;

public sealed class LinkProject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string DataFolder { get; init; }

    /// <summary>Read from older projects only: the current task is the first line of the project note now.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentTask { get; set; }

    /// <summary>
    /// The project note: its first line is what the user works on now, the rest what the project stands on (goal,
    /// decisions, facts). It goes with questions and tasks as a chip; null until the user writes it.
    /// </summary>
    public ProjectState? State { get; set; }

    /// <summary>The folder the project's work lives in, chosen by the user; null until chosen.</summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>The read-only runs' rules of an older Link: <see cref="Normalize"/> moves them into <see cref="AgentDoTools"/>.</summary>
    public List<string> AgentReadTools { get; set; } = [];

    /// <summary>Tool rules Claude Code may use without asking here: builds, exports, running code; "always for this project" adds here.</summary>
    public List<string> AgentDoTools { get; set; } = [];

    /// <summary>
    /// The user's own check of the workspace (a build, a linter, smoke tests), run as written after the agent's edits;
    /// null runs none.
    /// </summary>
    public string? CheckCommand { get; set; }

    public ChatConversationLink? LinkedConversation { get; set; }
    public ProjectResponse? LastResponse { get; set; }

    /// <summary>The ChatGPT chats this project has talked in, the panel's list of chats; the linked one is among them.</summary>
    public List<ProjectChat> Chats { get; set; } = [];

    /// <summary>Puts a chat the project talks in at the top of its list, or brings its title and time up to date.</summary>
    public void RememberChat(ChatConversationLink link)
    {
        if (link.IsNew) return;
        Chats.RemoveAll(chat => chat.ConversationId == link.ConversationId);
        Chats.Insert(0, new ProjectChat { ConversationId = link.ConversationId, Title = link.Title, Url = link.Url });
        if (Chats.Count > MaxChats) Chats.RemoveRange(MaxChats, Chats.Count - MaxChats);
    }

    private const int MaxChats = 40;

    /// <summary>
    /// Settles what older or hand-edited JSON may leave blank. The rules of an older Link's read-only list join the one
    /// list: no run is read only now.
    /// </summary>
    public void Normalize()
    {
        // The task and the state of older projects become one note, the task its first line.
        if (!string.IsNullOrWhiteSpace(CurrentTask))
        {
            var task = CurrentTask.Trim();
            State = new ProjectState
            {
                Text = State is { } state ? $"{task}\n{state.Text}" : task,
                UpdatedAt = State?.UpdatedAt ?? DateTimeOffset.Now
            };
        }

        CurrentTask = null;
        // An older project knew only its linked chat: the list of chats starts with it.
        Chats ??= [];
        Chats.RemoveAll(chat => string.IsNullOrWhiteSpace(chat.ConversationId));
        if (LinkedConversation is { IsNew: false } linked && Chats.All(chat => chat.ConversationId != linked.ConversationId))
        {
            Chats.Insert(0, new ProjectChat { ConversationId = linked.ConversationId, Title = linked.Title, Url = linked.Url });
        }

        CheckCommand = string.IsNullOrWhiteSpace(CheckCommand) ? null : CheckCommand.Trim();
        AgentDoTools.AddRange(AgentReadTools.Where(rule => !AgentDoTools.Contains(rule)));
        AgentReadTools.Clear();
    }

    public override string ToString() => Name;
}

/// <summary>The project note: what the user works on now (its first line), decisions and key facts, and when it was written.</summary>
public sealed class ProjectState
{
    public required string Text { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>The last assistant answer Link received from a project's linked conversation.</summary>
public sealed class ProjectResponse
{
    public required string ConversationId { get; init; }
    public required string Text { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>A question to a project's chat and its answer, kept in the project's history.</summary>
public sealed class ProjectExchange
{
    public required string Question { get; init; }
    public required string Answer { get; init; }
    public DateTimeOffset AnsweredAt { get; init; } = DateTimeOffset.Now;

    /// <summary>The ChatGPT chat it was asked in; null in the history of an earlier Link.</summary>
    public string? ConversationId { get; init; }

    /// <summary>What went along besides the words, as the chips named it ("Screen · Unreal Editor"); empty when nothing.</summary>
    public string Carried { get; init; } = string.Empty;
}

/// <summary>A ChatGPT chat of the project, as the panel lists it.</summary>
public sealed class ProjectChat
{
    public required string ConversationId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public DateTimeOffset LastUsedAt { get; init; } = DateTimeOffset.Now;
}
