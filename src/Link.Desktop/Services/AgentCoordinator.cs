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
        // A stopped run is compared too: the user sees what it changed before Stop.
        var changes = await _changes.CompareAsync(before, CancellationToken.None);
        return new AgentRun(task, before, result, changes);
    }
}
