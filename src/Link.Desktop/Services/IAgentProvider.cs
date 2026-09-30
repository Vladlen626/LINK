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

    /// <summary>Whether the CLI is found and signed in, and its version. Runs the CLI, so call it off the UI path.</summary>
    Task<AgentProviderStatus> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs the task and reports its events as they come. Cancelling stops the agent and its child processes; the
    /// result then says it was stopped. Throws when the CLI is not found or cannot start.
    /// </summary>
    Task<AgentResult> RunAsync(AgentTask task, IProgress<AgentEvent> progress, CancellationToken cancellationToken);
}
