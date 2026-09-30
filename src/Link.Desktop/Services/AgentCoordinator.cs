using System.Text;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>What the confirmation card settled: who works, where, with what access, on what goal.</summary>
public sealed record AgentPlan(
    AgentProviderKind Provider,
    string Workspace,
    AgentAccess Access,
    bool PlanFirst,
    bool BackupBeforeDo,
    string Goal);

/// <summary>A run of an agent as Activity shows it: the task, what the workspace was before and how it ended.</summary>
public sealed record AgentRun(AgentTask Task, WorkspaceBefore Before, AgentResult Result, IReadOnlyList<AgentChange> Changes);

/// <summary>
/// Runs agent tasks for the window: builds the task from an input snapshot and the confirmation card, notes the
/// workspace before Do, runs the provider and finds what changed. One task at a time.
/// </summary>
public sealed class AgentCoordinator
{
    private readonly IReadOnlyDictionary<AgentProviderKind, IAgentProvider> _providers;
    private readonly AgentChanges _changes;

    public AgentCoordinator(IEnumerable<IAgentProvider> providers, AgentChanges changes)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
        _changes = changes;
    }

    public IAgentProvider Provider(AgentProviderKind kind) => _providers[kind];

    public IEnumerable<IAgentProvider> Providers => _providers.Values;

    public AgentChanges Changes => _changes;

    /// <summary>
    /// The task the card describes: the goal first, then what Link knows of the project (task and state) and the
    /// snapshot's text context; its screenshots go as images. The project's read tools are always allowed, its Do tools
    /// only in Do.
    /// </summary>
    public static AgentTask BuildTask(AgentPlan plan, InputSnapshot snapshot, LinkProject project)
    {
        var prompt = new StringBuilder(plan.Goal.Trim());
        if (snapshot.CurrentTask.Length > 0) prompt.Append("\n\nCurrent task of the project: ").Append(snapshot.CurrentTask);
        var images = new List<string>();
        foreach (var item in snapshot.Context.Append(snapshot.Screen).OfType<ContextItem>())
        {
            if (item.FilePath is { } file && File.Exists(file))
            {
                if (item.Type == ContextItemType.Screenshot || item.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) images.Add(file);
                else prompt.Append("\n\nAttached file (").Append(item.Label).Append("): ").Append(file);
            }
            else if (item.Text is { Length: > 0 } text)
            {
                prompt.Append("\n\n").Append(item.Label).Append(":\n").Append(text);
            }
        }

        var tools = plan.Access == AgentAccess.Do ? project.AgentReadTools.Concat(project.AgentDoTools) : project.AgentReadTools;
        return new AgentTask
        {
            Provider = plan.Provider,
            Workspace = plan.Workspace,
            Prompt = prompt.ToString(),
            Access = plan.Access,
            PlanOnly = plan.PlanFirst,
            Images = images,
            AllowedTools = tools.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    /// <summary>
    /// Runs a task and returns it with what it changed. <paramref name="previous"/>: the run this one continues, whose
    /// "before" still holds. A copy is made before Do in a workspace without git when <paramref name="backup"/> says so.
    /// </summary>
    public async Task<AgentRun> RunAsync(
        AgentTask task,
        bool backup,
        AgentRun? previous,
        IProgress<AgentEvent> progress,
        CancellationToken cancellationToken)
    {
        var before = previous?.Before ?? await _changes.CaptureAsync(
            task.Workspace,
            backup && task.Access == AgentAccess.Do && !task.PlanOnly,
            cancellationToken);
        var result = await Provider(task.Provider).RunAsync(task, progress, cancellationToken);
        var written = task.AnswerFile is { } file && result.Succeeded
            ? await WriteAnswerAsync(task.Workspace, file, result.Summary, progress)
            : null;
        // A stopped run is compared too: the user sees what it changed before Stop.
        var changes = await _changes.CompareAsync(before, CancellationToken.None);
        // Without git and a copy nothing else can be told, but the file Link wrote is known.
        if (written is not null && !changes.Any(change => change.Path.Equals(written.Path, StringComparison.OrdinalIgnoreCase)))
        {
            changes = [.. changes, written];
        }

        return new AgentRun(task, before, result, changes);
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
