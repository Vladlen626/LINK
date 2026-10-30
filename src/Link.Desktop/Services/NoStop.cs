using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Night mode (No-stop in the code and the saved data): a task that takes at each stop the choice it recommends (the
/// first option of its question, the decision's button, the next step it named) after a short countdown the user can
/// cancel, until a stop rule makes it wait for the user after all. Its runs ask for nothing (<see cref="AgentTask.NoStop"/>).
/// </summary>
public static class NoStop
{
    /// <summary>What the user sees it as.</summary>
    public const string Name = "Night mode";

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
        if (record.NoStopTaken >= turns) return $"{Name} took {turns} choices in a row";
        if (string.Equals(choice, last, StringComparison.Ordinal)) return "the same choice came twice in a row";
        if (record.Runs.Count >= IdleRuns && record.Runs.TakeLast(IdleRuns).All(run => run.Files.Count == 0))
        {
            return $"{IdleRuns} runs in a row changed no file";
        }

        return null;
    }

    /// <summary>
    /// Whether the deny list holds a tool a running agent asks for: a rule names the tool (<c>WebFetch</c>), a command
    /// (<c>Bash(git push)</c>) or a command's start (<c>Bash(git push:*)</c>), in any part of a chained command.
    /// </summary>
    public static bool Denies(IEnumerable<string> deny, AgentPermissionAsk ask) => deny.Any(rule =>
    {
        var open = rule.IndexOf('(');
        if (open < 0 || !rule.EndsWith(')')) return string.Equals(rule, ask.Tool, StringComparison.Ordinal);
        if (!string.Equals(rule[..open], ask.Tool, StringComparison.Ordinal)) return false;
        var content = rule[(open + 1)..^1];
        return ask.What.Split(["&&", "||", ";", "|", "\n"], StringSplitOptions.TrimEntries).Any(part => content.EndsWith(":*", StringComparison.Ordinal)
            ? part.StartsWith(content[..^2], StringComparison.Ordinal)
            : string.Equals(part, content, StringComparison.Ordinal));
    });
}
