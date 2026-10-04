using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// A command-line agent that works in a workspace: Claude Code or Codex. Its own sign-in, tools and permissions do
/// the work; Link starts it, follows its events and stops it.
/// </summary>
public interface IAgentProvider
{
    AgentProviderKind Kind { get; }

    /// <summary>The name the user sees: "Claude Code", "Codex".</summary>
    string Name { get; }

    /// <summary>The effort levels its CLI takes, lowest first; the card offers them beside the CLI's own default.</summary>
    IReadOnlyList<AgentChoice> Efforts { get; }

    /// <summary>The models Settings offers beside the CLI's own default; reading them may touch the disk.</summary>
    IReadOnlyList<AgentChoice> Models { get; }

    /// <summary>The effort levels <paramref name="model"/> takes, lowest first, each with what it is for; null: the CLI's own model.</summary>
    IReadOnlyList<AgentChoice> EffortsOf(string? model);

    /// <summary>The model and effort the CLI runs on when Link leaves them to it, as its own config says; null when unknown.</summary>
    (string? Model, string? Effort) OwnSetup { get; }

    /// <summary>
    /// Whether the CLI is found and signed in, and its version; <paramref name="chosenPath"/> is a file chosen for it,
    /// null finds it. Runs the CLI, so call it off the UI path.
    /// </summary>
    Task<AgentProviderStatus> CheckAsync(string? chosenPath, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the task and reports its events as they come. Cancelling stops the agent and its child processes; the
    /// result then says it was stopped. Throws when the CLI is not found or cannot start.
    /// </summary>
    Task<AgentResult> RunAsync(AgentTask task, IProgress<AgentEvent> progress, CancellationToken cancellationToken);

    /// <summary>
    /// The subscription's usage windows as they stand now, asked of the CLI without a model run (Claude Code's /usage,
    /// Codex's app-server); none when it cannot tell, such as without a subscription. Runs the CLI: off the UI path.
    /// </summary>
    Task<IReadOnlyList<AgentLimit>> ReadLimitsAsync(CancellationToken cancellationToken);
}
