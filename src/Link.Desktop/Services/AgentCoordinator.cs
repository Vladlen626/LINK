using System.Text;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>What the confirmation card settled: who works, where, with what access and effort, on what goal.</summary>
public sealed record AgentPlan(
    AgentProviderKind Provider,
    string Workspace,
    AgentAccess Access,
    bool PlanFirst,
    bool BackupBeforeDo,
    string Goal)
{
    /// <summary>The model chosen on the card, one of the agent's own; null: the CLI's own.</summary>
    public string? Model { get; init; }

    /// <summary>The effort chosen on the card; null: the CLI's own, or Link's pick when <see cref="AutoEffort"/> is on.</summary>
    public string? Effort { get; init; }

    /// <summary>Auto on the card: Link picks the effort of each run (see <see cref="Services.AutoEffort"/>).</summary>
    public bool AutoEffort { get; init; }

    /// <summary>Auto on the card: with edits, nothing is asked (see <see cref="AgentTask.AutoApprove"/>).</summary>
    public bool AutoApprove { get; init; }

    /// <summary>The specialist chosen on the card, the file name of its role; null: General.</summary>
    public string? RoleId { get; init; }

    /// <summary>The skills on the card, which the agent is told to read first.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];
}

/// <summary>
/// A run of an agent as Activity shows it: the task, what the workspace was before, how it ended and, after Do, the
/// project's last check.
/// </summary>
public sealed record AgentRun(
    AgentTask Task,
    WorkspaceBefore Before,
    AgentResult Result,
    IReadOnlyList<AgentChange> Changes,
    AgentCheckResult? Check = null);

/// <summary>
/// Runs agent tasks for the window: builds the task from an input snapshot and the confirmation card, notes the
/// workspace before Do, runs the provider, checks the result with the project's command and finds what changed. One
/// task at a time.
/// </summary>
public sealed class AgentCoordinator
{
    /// <summary>How many times a failed check goes back to the agent before the user hears of it.</summary>
    public const int CheckRetries = 2;

    private readonly IReadOnlyDictionary<AgentProviderKind, IAgentProvider> _providers;
    private readonly AgentChanges _changes;
    private readonly Action<string> _log;

    public AgentCoordinator(IEnumerable<IAgentProvider> providers, AgentChanges changes, Action<string> log, AgentConsole? console = null)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
        _changes = changes;
        _log = log;
        Console = console ?? new AgentConsole();
    }

    public IAgentProvider Provider(AgentProviderKind kind) => _providers[kind];

    public IEnumerable<IAgentProvider> Providers => _providers.Values;

    public AgentChanges Changes => _changes;

    /// <summary>What the providers sent their CLIs and what these wrote back, for the Console window.</summary>
    public AgentConsole Console { get; }

    /// <summary>
    /// The task the card describes: the goal first, then what Link knows of the project (task and state) and the
    /// snapshot's text context; its screenshots go as images. The project's read tools are always allowed, its Do tools
    /// only in Do. With edits and a check, the agent is told that Link runs the check, as the card says.
    /// </summary>
    public static AgentTask BuildTask(AgentPlan plan, InputSnapshot snapshot, LinkProject project)
    {
        var (words, images) = Words(snapshot with { Text = plan.Goal }, newTask: true);
        var prompt = new StringBuilder(words);
        if (plan.Access == AgentAccess.Do && project.CheckCommand is { } check) prompt.Append("\n\n").Append(CheckNote(check));
        return new AgentTask
        {
            Provider = plan.Provider,
            Workspace = plan.Workspace,
            Prompt = prompt.ToString(),
            Access = plan.Access,
            PlanOnly = plan.PlanFirst,
            Images = images,
            AllowedTools = Tools(project, plan.Workspace, plan.Access),
            CheckCommand = project.CheckCommand,
            AutoApprove = plan.AutoApprove && plan.Access == AgentAccess.Do,
            Model = plan.Model,
            Effort = plan.Effort,
            RoleId = plan.RoleId,
            Skills = plan.Skills
        };
    }

    /// <summary>
    /// A snapshot as words for the agent: its text first, then its text context and its files by path; its screenshots
    /// and pictures go apart, as images. The project note and a task's result go with a new task only: a session that
    /// goes on has the note, and its own result is for the chat.
    /// </summary>
    public static (string Words, IReadOnlyList<string> Images) Words(InputSnapshot snapshot, bool newTask)
    {
        var words = new StringBuilder(snapshot.Text.Trim());
        var images = new List<string>();
        foreach (var item in snapshot.Context.Append(snapshot.Screen).OfType<ContextItem>())
        {
            if (!newTask && item.Type is ContextItemType.AgentResult or ContextItemType.ProjectState) continue;
            if (item.FilePath is { } file && File.Exists(file))
            {
                if (item.Type == ContextItemType.Screenshot || item.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) images.Add(file);
                else words.Append("\n\nAttached file (").Append(item.Label).Append("): ").Append(file);
            }
            else if (item.Text is { Length: > 0 } text)
            {
                words.Append("\n\n").Append(item.Label).Append(":\n").Append(text);
            }
        }

        return (words.ToString(), images);
    }

    /// <summary>
    /// The tool rules Claude Code may use without asking: the project's read list, its Do list with edits, and in a
    /// Perforce workspace the p4 commands its rules ask for.
    /// </summary>
    public static IReadOnlyList<string> Tools(LinkProject project, string workspace, AgentAccess access) =>
        (access == AgentAccess.Do ? project.AgentReadTools.Concat(project.AgentDoTools) : project.AgentReadTools)
        .Concat(PerforceTools(workspace, access))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// In a Perforce workspace: the p4 commands that only look, always, and those that open files for the agent's own
    /// edits, with edits. Submitting, reverting and syncing stay the user's. Codex is let through its sandbox instead.
    /// </summary>
    public static IReadOnlyList<string> PerforceTools(string workspace, AgentAccess access)
    {
        if (!AgentChanges.IsPerforce(workspace)) return [];
        string[] looking = ["opened", "diff", "fstat", "filelog", "where", "have", "info", "changes", "describe", "status"];
        string[] opening = ["edit", "add", "delete", "move"];
        return (access == AgentAccess.Do ? looking.Concat(opening) : looking).Select(command => $"Bash(p4 {command}:*)").ToArray();
    }

    /// <summary>
    /// The user's reply after a run: the same session goes on with it, with the same access, folder, model and check,
    /// and without the first task's images. Null without a session or a reply.
    /// </summary>
    public static AgentTask? ContinueTask(AgentRun run, string reply) =>
        run.Result.SessionId is { } session && reply.Trim() is { Length: > 0 } text
            ? run.Task with { ResumeSessionId = session, Prompt = text, Images = [] }
            : null;

    /// <summary>
    /// The brief another agent takes the task over with. Neither CLI can resume the other's session, so the new one is
    /// told, from the task's record: the task as it began, what the user said on the way, the first agent's plan, the
    /// files changed (with lines added and removed where git tells them), the last check, how the first agent ended and
    /// its last steps. Kept to about <see cref="BriefBudget"/> characters: steps go first, the task and the user's words
    /// never.
    /// </summary>
    public static string HandOffBrief(AgentTaskRecord record, string firstName, string? numstat)
    {
        var said = record.Entries.Where(entry => entry.Kind == AgentEntryKind.You).Select(entry => "- " + entry.Text).ToArray();
        var plan = record.Plan.Select(item => $"- [{(item.Done ? "x" : " ")}] {item.Text}").ToArray();
        var files = numstat ?? string.Join('\n', record.ChangedFiles.Select(file => "- " + file));
        var words = AgentNext.Split(record.LastWords).Text.Trim();
        var steps = record.Steps.TakeLast(15).ToList();
        while (true)
        {
            var brief = new StringBuilder(record.Brief.Trim())
                .Append($"\n\nYou take this task over from {firstName}, which worked on it in this workspace and stopped before it was done. ")
                .Append("Its changes are in the workspace, finished or not: look at them first (git diff or p4 diff, as the workspace has), and do not redo what is done.");
            if (said.Length > 0) brief.Append("\n\nWhat the user said on the way, oldest first:\n").AppendJoin('\n', said);
            if (plan.Length > 0) brief.Append($"\n\n{firstName}'s plan as it last stood:\n").AppendJoin('\n', plan);
            if (files.Length > 0) brief.Append("\n\nFiles changed so far").Append(numstat is null ? ":\n" : " (lines added, removed, file):\n").Append(files);
            if (record.LastCheck is { } check) brief.Append("\n\nThe project's check as it last ran:\n").Append(check);
            if (words.Length > 0) brief.Append($"\n\nHow {firstName} ended:\n").Append(words);
            if (steps.Count > 0) brief.Append("\n\nIts last steps, oldest first:\n").AppendJoin('\n', steps.Select(step => "- " + step));
            if (brief.Length <= BriefBudget || steps.Count == 0 && words.Length <= 1500) return brief.ToString();
            if (steps.Count > 0) steps.RemoveAt(0);
            else words = words[..1500] + "…";
        }
    }

    /// <summary>About 1.5k tokens: what a brief may take before its oldest steps go.</summary>
    public const int BriefBudget = 6000;

    /// <summary>The other agent takes the task over with the brief the user saw and maybe edited: a new session of its own.</summary>
    public static AgentTask HandOffTask(AgentTask first, AgentProviderKind to, string brief, IReadOnlyList<string> images) =>
        first with
        {
            Provider = to,
            ResumeSessionId = null,
            Prompt = brief.Trim(),
            Images = images,
            PlanOnly = false,
            AnswerFile = null,
            AnswerIsSkill = false
        };

    /// <summary>
    /// The first agent takes its task back, such as once its limit has reset: its own session goes on, told what the
    /// other agent did meanwhile, so that it does not learn the task anew.
    /// </summary>
    public static string ReturnBrief(AgentTaskRecord record, string otherName, string? numstat)
    {
        var brief = new StringBuilder($"While you were away, {otherName} worked on this task in the same workspace.");
        var files = numstat ?? string.Join('\n', record.ChangedFiles.Select(file => "- " + file));
        if (files.Length > 0) brief.Append("\n\nFiles changed so far:\n").Append(files);
        if (record.LastCheck is { } check) brief.Append("\n\nThe project's check as it last ran:\n").Append(check);
        if (AgentNext.Split(record.LastWords).Text.Trim() is { Length: > 0 } words) brief.Append($"\n\nHow {otherName} ended:\n").Append(words.Length <= 2000 ? words : words[..2000] + "…");
        return brief.Append("\n\nLook at its changes, then finish the task.").ToString();
    }

    /// <summary>
    /// Runs a task and returns it with what it changed. <paramref name="previous"/>: the run this one continues, whose
    /// "before" still holds. A copy is made before Do in a workspace without git when <paramref name="backup"/> says so.
    /// After Do the project's check runs; while it fails, the agent continues its session with the check's output, up to
    /// <see cref="CheckRetries"/> times, and the run returned is the last one with the numbers of all. A skill of a name
    /// the workspace has already is written over only when <paramref name="replaceSkill"/> says yes.
    /// </summary>
    public async Task<AgentRun> RunAsync(
        AgentTask task,
        bool backup,
        AgentRun? previous,
        IProgress<AgentEvent> progress,
        CancellationToken cancellationToken,
        Func<string, bool>? replaceSkill = null)
    {
        var before = previous?.Before ?? await _changes.CaptureAsync(
            task.Workspace,
            backup && task.Access == AgentAccess.Do && !task.PlanOnly,
            cancellationToken);
        var provider = Provider(task.Provider);
        var result = await provider.RunAsync(task, progress, cancellationToken);
        AgentCheckResult? check = null;
        for (var retry = 0; task.CheckCommand is { } command && task.Access == AgentAccess.Do && !task.PlanOnly && result.Succeeded; retry++)
        {
            check = await CheckAsync(command, task.Workspace, retry, progress, cancellationToken);
            if (!check.ForTheAgent || retry == CheckRetries || result.SessionId is not { } session) break;
            task = task with { ResumeSessionId = session, Prompt = CheckFailed(command, check), Images = [] };
            result = Continued(result, await provider.RunAsync(task, progress, cancellationToken));
        }

        var answerFile = !result.Succeeded ? null
            : task.AnswerIsSkill ? SkillFile(task.Workspace, result.Summary, replaceSkill, progress)
            : task.AnswerFile;
        var written = answerFile is not null ? await WriteAnswerAsync(task.Workspace, answerFile, result.Summary, progress) : null;
        IReadOnlyList<AgentChange> changes;
        try
        {
            // A stopped run is compared too: the user sees what it changed before Stop.
            changes = await _changes.CompareAsync(before, CancellationToken.None);
        }
        catch (IOException exception)
        {
            // Perforce gone during the run (a ticket expired): the agent's answer stays, its files are not known.
            progress.Report(new AgentEvent(AgentEventKind.Error, $"{exception.Message} The files this run changed are not listed."));
            changes = [];
        }

        // Without git and a copy nothing else can be told, but the file Link wrote is known.
        if (written is not null && !changes.Any(change => change.Path.Equals(written.Path, StringComparison.OrdinalIgnoreCase)))
        {
            changes = [.. changes, written];
        }

        return new AgentRun(task, before, result, changes, check);
    }

    /// <summary>
    /// Start over with a note: the run's session is asked once, read only, what it tried and why it did not work; the
    /// task returned is for a new session, with the original goal and images, that note, and the access, folder and
    /// check of the run. Without a session to ask it goes without a note; null when Stop ended the asking.
    /// </summary>
    public async Task<(AgentTask Task, string Note)?> StartOverAsync(
        AgentRun run,
        AgentTask origin,
        (string? Model, string? Effort) asking,
        CancellationToken cancellationToken)
    {
        var note = string.Empty;
        if (run.Result.SessionId is { } session)
        {
            var asked = await Provider(run.Task.Provider).RunAsync(run.Task with
            {
                ResumeSessionId = session,
                Access = AgentAccess.Analyze,
                PlanOnly = false,
                Prompt = NoteRequest,
                Images = [],
                AllowedTools = [],
                AnswerFile = null,
                MaxTurns = 3,
                Model = asking.Model,
                Effort = asking.Effort
            }, new Progress<AgentEvent>(), cancellationToken);
            if (asked.Stopped) return null;
            if (asked.Succeeded) note = asked.Summary.Trim();
        }

        var prompt = note.Length == 0 ? origin.Prompt : $"{origin.Prompt}\n\nA note from an earlier attempt at this task, which did not work out:\n{note}";
        return (run.Task with { ResumeSessionId = null, Prompt = prompt, Images = origin.Images }, note);
    }

    /// <summary>
    /// Save as skill: the run's session, read only, answers with a SKILL.md of what it has just done, and Link writes it
    /// (see <see cref="SkillFile"/>). The request is the task file with the workspace's skills, so that the same work
    /// keeps its name. Null without a session to ask.
    /// </summary>
    public static AgentTask? SkillTask(AgentRun run, string request)
    {
        if (run.Result.SessionId is not { } session) return null;
        var skills = ProjectSkills.In(run.Task.Workspace).Select(skill => skill.Name).ToArray();
        return run.Task with
        {
            ResumeSessionId = session,
            Access = AgentAccess.Analyze,
            PlanOnly = false,
            Prompt = $"{request.Trim()}\n\nSkills of the workspace: {(skills.Length == 0 ? "none yet" : string.Join(", ", skills))}.",
            Images = [],
            AllowedTools = [],
            AnswerFile = null,
            AnswerIsSkill = true,
            CheckCommand = null,
            MaxTurns = 10
        };
    }

    /// <summary>
    /// What the session goes on with when the user speaks up while the agent works: neither CLI takes words during a
    /// run, so Link stops it and the same session goes on with them.
    /// </summary>
    public static string Interrupted(string words) =>
        $"The user stopped you to say this:\n{words.Trim()}\nTake it into account and go on with the task.";

    /// <summary>The one short request to a session that goes nowhere: its note for the clean session after it.</summary>
    internal const string NoteRequest =
        "Stop working on the task and change nothing. In five lines at most, write what you tried and why it did not work: " +
        "a note for a new session that takes the task over from the start.";

    /// <summary>Runs the project's check and tells Activity and link.log how it went; the command and output stay out of the log.</summary>
    private async Task<AgentCheckResult> CheckAsync(string command, string workspace, int retry, IProgress<AgentEvent> progress, CancellationToken cancellationToken)
    {
        progress.Report(new AgentEvent(AgentEventKind.Status, $"Check: {command}"));
        var check = await AgentCheck.RunAsync(command, workspace, AgentCheck.TimeLimit, cancellationToken);
        var again = check.ForTheAgent && retry < CheckRetries ? $"; back to the agent ({retry + 1} of {CheckRetries})" : string.Empty;
        var text = $"Check {check} in {CliAgentProvider.Seconds(check.Duration)}{again}";
        progress.Report(new AgentEvent(check.Passed ? AgentEventKind.Status : AgentEventKind.Error, text));
        _log($"[Agent] check {(retry == 0 ? "after Do" : $"after retry {retry}")}: {check}, {CliAgentProvider.Seconds(check.Duration)}");
        return check;
    }

    /// <summary>
    /// What the agent is told of the check with its task. Told nothing, Codex ran a browser check in its own sandbox,
    /// where it cannot work, for half an hour, and changed the check and the project's server to get it going.
    /// </summary>
    public static string CheckNote(string command) =>
        $"When you finish, Link runs the project's check `{command}` itself and gives you its output if it fails: " +
        "you need not run it, and leave it as it is.";

    /// <summary>What the agent is told when the check fails: the command, its exit and the end of its output.</summary>
    internal static string CheckFailed(string command, AgentCheckResult check) =>
        $"Link ran the project's check after your changes: `{command}` {check}. The end of its output:\n" +
        $"```\n{check.Tail}\n```\nFix the cause. Link runs the check again when you finish.";

    /// <summary>A run and the run that continued it, as one task: the last session and summary, everything counted.</summary>
    private static AgentResult Continued(AgentResult first, AgentResult next)
    {
        var log = first.Log + next.Log;
        return next with
        {
            Denials = first.Denials.Concat(next.Denials).Distinct().ToArray(),
            ChangedFiles = first.ChangedFiles.Concat(next.ChangedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Stats = first.Stats.Add(next.Stats),
            Loops = first.Loops.Concat(next.Loops).Distinct().ToArray(),
            Log = log.Length <= AgentProcess.LogLimit ? log : log[^AgentProcess.LogLimit..]
        };
    }

    /// <summary>The answer without the code block it may have come in: ```markdown … ```.</summary>
    internal static string Unfenced(string answer)
    {
        var text = answer.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal) || !text.EndsWith("```", StringComparison.Ordinal)) return text;
        var start = text.IndexOf('\n');
        var end = text.LastIndexOf("```", StringComparison.Ordinal);
        return start < 0 || end <= start ? text : text[(start + 1)..end].Trim();
    }

    /// <summary>
    /// Where a skill the agent answered with goes: by the name of its front matter, which must be one both agents accept.
    /// Null writes nothing: a bad name, or a skill of that name that is to stay.
    /// </summary>
    private static string? SkillFile(string workspace, string answer, Func<string, bool>? replace, IProgress<AgentEvent> progress)
    {
        var name = ProjectSkills.Header(Unfenced(answer)).Name ?? string.Empty;
        if (!ProjectSkills.IsName(name))
        {
            progress.Report(new AgentEvent(AgentEventKind.Error,
                $"The skill's name \"{name}\" is not lowercase Latin letters, digits and hyphens: nothing was written."));
            return null;
        }

        var file = ProjectSkills.RelativePath(name);
        if (File.Exists(Path.Combine(workspace, file)) && replace?.Invoke(name) != true)
        {
            progress.Report(new AgentEvent(AgentEventKind.Status, $"The skill {name} stays as it was: nothing was written."));
            return null;
        }

        return file;
    }

    /// <summary>Writes a read-only task's answer to its file: the one change, made by Link and shown in Activity.</summary>
    private static async Task<AgentChange?> WriteAnswerAsync(string workspace, string file, string answer, IProgress<AgentEvent> progress)
    {
        var text = Unfenced(answer);
        if (text.Length == 0)
        {
            progress.Report(new AgentEvent(AgentEventKind.Error, $"The agent gave no answer to write to {file}."));
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(workspace, file));
        var existed = File.Exists(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            progress.Report(new AgentEvent(AgentEventKind.Error, $"Could not write {file}: {exception.Message}"));
            return null;
        }

        progress.Report(new AgentEvent(AgentEventKind.Status, $"Link wrote {file}: {text.Split('\n').Length} lines"));
        return new AgentChange(path, existed ? "M" : "??", true);
    }
}
