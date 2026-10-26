using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// What the Telegram bot may see and do of Link: its projects and tasks, a new task, words, a
/// decision, Stop and No-stop, a run's review window, which Claude account runs; the bot also a run's pictures and
/// changes. Never the screen, the microphone, the ChatGPT chat or other settings. The window answers on its own thread; an action returns null when done, else why not.
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

    /// <summary>Words for a task, with pictures the user sent (files Link saved) when there are any.</summary>
    Task<string?> ReplyAsync(Guid id, string words, string device, IReadOnlyList<string>? images = null);

    Task<string?> DecideAsync(Guid id, bool always, string device);

    /// <summary>The decision the task waits on is refused, as the card's Deny does.</summary>
    Task<string?> RefuseAsync(Guid id, string device);

    Task<string?> StopAsync(Guid id, string device);
    Task<string?> SetNoStopAsync(Guid id, bool on, string device);

    /// <summary>What the agents spent as ccusage last counted it; null before it has.</summary>
    Task<RemoteUsage?> UsageAsync();

    /// <summary>The pictures the task's last run made, as files on this computer: for the Telegram bot, never the page.</summary>
    Task<IReadOnlyList<string>> ImagesAsync(Guid id);

    /// <summary>The changes of the task's last run as a diff; null when there are none to show.</summary>
    Task<string?> DiffAsync(Guid id);

    /// <summary>
    /// A page of the task's last run (<see cref="RemoteTaskView.Pages"/>, a model's review window), served again if it
    /// stopped: its address on this computer, else null.
    /// </summary>
    Task<Uri?> OpenPageAsync(Guid id, int page);

    /// <summary>The page's reply (Apply marks) for the task, with the marks Link fetched for it; null when sent, else why not.</summary>
    Task<string?> ApplyPageAsync(Guid id, int page, string device);

    /// <summary>Claude Code's accounts and the one new runs use.</summary>
    Task<RemoteAccounts> AccountsAsync();

    /// <summary>New runs use this Claude account (null: the user's own), as Settings would set it.</summary>
    Task<string?> SetAccountAsync(string? account, string device);
}

/// <summary>Claude's accounts by name, the user's own as null, and the one in use.</summary>
public sealed record RemoteAccounts(IReadOnlyList<string?> Names, string? Active);

/// <summary>A page the last run opened: its button, and the words its second button sends (null: none).</summary>
public sealed record RemoteTaskPage(string Label, string? ReplyLabel, string? ReplyText);

public sealed record RemoteUsage(string Line, string Hint);

public sealed record RemoteProject(Guid Id, string Name, bool Active, bool HasWorkspace);

/// <summary>A task in the list: <see cref="Waits"/> says what it waits for from the user, null for nothing.</summary>
public sealed record RemoteTask(
    Guid Id, string Project, string Goal, string Status, string? Agent, bool Working, string? Waits, bool NoStop, DateTimeOffset Updated, string LastWords);

/// <summary>
/// An open task: its last words and history, and what it waits for (a question, a decision, a limit). <see cref="Now"/> is
/// the step under way while it runs; <see cref="Before"/> its course in short lines (the user's words, each run and what it
/// did, the last run's answer in full), for a small screen; <see cref="Pages"/> the pages its last run opened.
/// </summary>
public sealed record RemoteTaskView(
    RemoteTask Task,
    IReadOnlyList<RemoteEntry> Entries,
    RemoteQuestion? Question,
    RemoteDecision? Decision,
    string? Limit,
    string? NoStopPending,
    IReadOnlyList<string> ChangedFiles,
    string Usage,
    bool TakesWords,
    string? Now,
    IReadOnlyList<AgentPlanItem> Plan,
    IReadOnlyList<string> Before,
    IReadOnlyList<RemoteTaskPage> Pages);

public sealed record RemoteEntry(DateTimeOffset At, string Kind, string Text);

public sealed record RemoteQuestion(string Text, IReadOnlyList<AgentOption> Options, bool MultiSelect);

public sealed record RemoteDecision(string Text, string Button, bool CanRemember, bool CanRefuse);

/// <summary>
/// A new task: its project (null: the active one), its words, and the card's two boxes; the bot may also choose the agent
/// and its level (null: as Settings say) and give pictures the user sent.
/// </summary>
public sealed record RemoteNewTask(
    Guid? ProjectId, string Goal, bool PlanFirst, bool NoStop, AgentProviderKind? Provider = null, AgentLevel? Level = null,
    IReadOnlyList<string>? Attached = null);
