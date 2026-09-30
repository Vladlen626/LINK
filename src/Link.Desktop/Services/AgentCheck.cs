using System.Diagnostics;

namespace Link.Desktop.Services;

public enum AgentCheckOutcome
{
    Passed,
    Failed,
    TimedOut,
    Stopped,
    NotStarted
}

/// <summary>How the project's check ended, with the last lines of what it printed for the agent to fix.</summary>
public sealed record AgentCheckResult(AgentCheckOutcome Outcome, int ExitCode, TimeSpan Duration, string Tail)
{
    public bool Passed => Outcome == AgentCheckOutcome.Passed;

    /// <summary>What the agent can fix: a failure or a hang, not a Stop or a command that would not start.</summary>
    public bool ForTheAgent => Outcome is AgentCheckOutcome.Failed or AgentCheckOutcome.TimedOut;

    public override string ToString() => Outcome switch
    {
        AgentCheckOutcome.Passed => "passed",
        AgentCheckOutcome.Failed => $"failed with exit code {ExitCode}",
        AgentCheckOutcome.TimedOut => "timed out",
        AgentCheckOutcome.Stopped => "stopped",
        _ => "could not start"
    };
}

/// <summary>
/// The project's check after Do: the user's own command (a build, a linter, smoke tests) run exactly as written in the
/// workspace, with a time limit; Stop ends it. Its output goes back to the agent only, never to link.log.
/// </summary>
public static class AgentCheck
{
    /// <summary>A build with its tests may take minutes; a check that hangs must not hold the task for ever.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(10);

    // The end of the output is where a build or a test run says what failed.
    private const int TailLines = 60;
    private const int LineLength = 400;

    public static async Task<AgentCheckResult> RunAsync(string command, string workspace, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var tail = new Queue<string>();
        void Keep(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line.Length <= LineLength ? line : line[..LineLength] + "…");
                if (tail.Count > TailLines) tail.Dequeue();
            }
        }

        var clock = Stopwatch.StartNew();
        AgentProcessResult result;
        try
        {
            result = await AgentProcess.RunAsync(AgentCommand.Shell(command, workspace), Keep, Keep, timeout.Token);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new AgentCheckResult(AgentCheckOutcome.NotStarted, -1, clock.Elapsed, exception.Message);
        }

        var outcome = !result.Stopped ? result.ExitCode == 0 ? AgentCheckOutcome.Passed : AgentCheckOutcome.Failed
            : cancellationToken.IsCancellationRequested ? AgentCheckOutcome.Stopped
            : AgentCheckOutcome.TimedOut;
        string output;
        lock (tail) output = string.Join('\n', tail).Trim();
        return new AgentCheckResult(outcome, result.ExitCode, clock.Elapsed, output);
    }
}
