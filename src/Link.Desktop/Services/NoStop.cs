using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// No-stop: a task that takes at each stop the choice it recommends (the first option of its question, the decision's
/// button, the next step it named) after a short countdown the user can cancel, until a stop rule makes it wait for
/// the user after all. Its runs ask for nothing (<see cref="AgentTask.NoStop"/>).
/// </summary>
public static class NoStop
{
    /// <summary>The seconds a choice shows before No-stop takes it: time for the user at the PC to step in.</summary>
    public const int CountdownSeconds = 10;

    /// <summary>The runs in a row without a changed file after which No-stop waits: the task goes round in circles.</summary>
    public const int IdleRuns = 3;

    /// <summary>The answer No-stop gives a question: its first option, the one the agent recommends; null for none.</summary>
    public static string? Pick(AgentQuestion question) => question.Options.FirstOrDefault()?.Label;

    /// <summary>The words that send the task on to the step its agent named.</summary>
    public static string GoOn(AgentNext next) => $"Go on: {next.Step}";

    /// <summary>The words that send the task on after a red check.</summary>
    public const string FixCheck = "The project's check fails: fix it, then go on with the task.";

    /// <summary>
    /// Why No-stop waits for the user instead of taking <paramref name="choice"/>; null: it takes it. <paramref name="last"/>
    /// is the choice it took the time before.
    /// </summary>
    public static string? WhyWait(AgentTaskRecord record, int turns, string choice, string? last)
    {
        if (record.NoStopTaken >= turns) return $"No-stop took {turns} choices in a row";
        if (string.Equals(choice, last, StringComparison.Ordinal)) return "the same choice came twice in a row";
        if (record.Runs.Count >= IdleRuns && record.Runs.TakeLast(IdleRuns).All(run => run.Files.Count == 0))
        {
            return $"{IdleRuns} runs in a row changed no file";
        }

        return null;
    }
}
