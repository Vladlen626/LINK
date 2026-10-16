namespace Link.Desktop.Services;

/// <summary>
/// What the remote page may see and do of Link: its projects and tasks, a new task, words, a decision, Stop and No-stop.
/// Never the screen, the microphone, the ChatGPT chat or settings. The window answers on its own thread; an action
/// returns null when done, else why not.
/// </summary>
public interface IRemoteHost
{
    /// <summary>Something a task shows changed: the page asks again.</summary>
    event Action? RemoteChanged;

    /// <summary>A task needs the user, or is through: the task, and a notification's title and words for the user's devices.</summary>
    event Action<Guid, string, string>? RemoteAlert;

    Task<IReadOnlyList<RemoteProject>> ProjectsAsync();
    Task<IReadOnlyList<RemoteTask>> TasksAsync();
    Task<RemoteTaskView?> TaskAsync(Guid id);
    Task<string?> StartAsync(RemoteNewTask task, string device);
    Task<string?> ReplyAsync(Guid id, string words, string device);
    Task<string?> DecideAsync(Guid id, bool always, string device);
    Task<string?> StopAsync(Guid id, string device);
    Task<string?> SetNoStopAsync(Guid id, bool on, string device);

    /// <summary>What the agents spent as ccusage last counted it; null before it has.</summary>
    Task<RemoteUsage?> UsageAsync();
}

public sealed record RemoteUsage(string Line, string Hint);

public sealed record RemoteProject(Guid Id, string Name, bool Active, bool HasWorkspace);

/// <summary>A task in the list: <see cref="Waits"/> says what it waits for from the user, null for nothing.</summary>
public sealed record RemoteTask(
    Guid Id, string Project, string Goal, string Status, string? Agent, bool Working, string? Waits, bool NoStop, DateTimeOffset Updated, string LastWords);

/// <summary>An open task: its last words and history, and what it waits for (a question, a decision, a limit).</summary>
public sealed record RemoteTaskView(
    RemoteTask Task,
    IReadOnlyList<RemoteEntry> Entries,
    RemoteQuestion? Question,
    RemoteDecision? Decision,
    string? Limit,
    string? NoStopPending,
    IReadOnlyList<string> ChangedFiles,
    string Usage,
    bool TakesWords);

public sealed record RemoteEntry(DateTimeOffset At, string Kind, string Text);

public sealed record RemoteQuestion(string Text, IReadOnlyList<AgentOption> Options, bool MultiSelect);

public sealed record RemoteDecision(string Text, string Button, bool CanRemember);

/// <summary>A new task from the page: its project (null: the active one), its words, and the card's two boxes.</summary>
public sealed record RemoteNewTask(Guid? ProjectId, string Goal, bool PlanFirst, bool NoStop);
