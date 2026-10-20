using System.Globalization;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// The project's last two weeks as the inspector's Reflect section reads them: what the runs cost, the dearest tasks,
/// loops, the same failure over again and the skills no run opened. From Link's own records, no model: a look back that
/// says where to tighten the harness, at no cost.
/// </summary>
public static class Reflection
{
    public static readonly TimeSpan Period = TimeSpan.FromDays(14);

    // A failure's first line is cut to this, so that the same one told with different details still groups.
    private const int ReasonShown = 80;

    // Too few tasks say nothing about a skill nobody opened.
    private const int TasksForSkills = 3;

    public static IReadOnlyList<string> Lines(IEnumerable<AgentTaskRecord> tasks, IReadOnlyList<string> skills, DateTimeOffset now)
    {
        var since = now - Period;
        var recent = tasks
            .Select(task => (Task: task, Runs: task.Runs.Where(run => run.StartedAt >= since).ToArray()))
            .Where(task => task.Runs.Length > 0 || task.Task.Entries.Any(entry => entry.Kind == AgentEntryKind.NoStart && entry.At >= since))
            .ToArray();
        if (recent.Length == 0) return [];

        var lines = new List<string>();
        var runs = recent.SelectMany(task => task.Runs).ToArray();
        var cost = runs.Sum(run => run.CostUsd ?? 0);
        lines.Add($"{Plural(recent.Length, "task")}, {Plural(runs.Length, "run")} in {Period.TotalDays:0} days" +
                  (cost > 0 ? $" · ≈ {Money(cost)} at API prices" : string.Empty));

        var dearest = recent.Where(task => task.Runs.Length > 0)
            .Select(task => (task.Task, task.Runs, Cost: task.Runs.Sum(run => run.CostUsd ?? 0), Tokens: task.Runs.Sum(Tokens)))
            .OrderByDescending(task => task.Cost).ThenByDescending(task => task.Tokens)
            .Take(3).ToArray();
        if (recent.Length > 1 && dearest.Length > 0)
        {
            lines.Add("Dearest:");
            lines.AddRange(dearest.Select(task =>
                $"  {(task.Cost > 0 ? Money(task.Cost) : AgentContext.Tokens(task.Tokens) + " tokens")} · {Plural(task.Runs.Length, "run")} · {Shortened(task.Task.Goal, 60)}"));
        }

        var looped = recent.Where(task => task.Runs.Any(run => run.Looped)).ToArray();
        if (looped.Length > 0)
        {
            lines.Add($"Looped in {Plural(looped.Length, "task")}: " + string.Join("; ", looped.Take(3).Select(task => Shortened(task.Task.Goal, 50))));
        }

        // The agent's own words on a failed run, or Link's when it could not start one: the same reason twice is a fix to make once.
        var reasons = recent.SelectMany(task => task.Runs.Where(run => run.Failed).Select(run => FirstLine(run.Words) ?? run.Outcome)
                .Concat(task.Task.Entries.Where(entry => entry.Kind == AgentEntryKind.NoStart && entry.At >= since).Select(entry => entry.Text)))
            .Select(reason => Shortened(FirstLine(reason) ?? string.Empty, ReasonShown))
            .Where(reason => reason.Length > 0)
            .GroupBy(reason => reason, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .Take(3)
            .ToArray();
        if (reasons.Length > 0)
        {
            lines.Add("Failed the same way:");
            lines.AddRange(reasons.Select(group => $"  × {group.Count()} {group.Key}"));
        }

        if (recent.Length >= TasksForSkills && skills.Count > 0)
        {
            var steps = runs.SelectMany(run => run.Steps).Select(step => step.Replace('\\', '/')).ToArray();
            // A file of the skill's folder read or run, or Claude Code's Skill tool on it.
            var unopened = skills.Where(skill => !steps.Any(step =>
                step.Contains($"skills/{skill}/", StringComparison.OrdinalIgnoreCase) || step.Contains($"Skill: {skill}", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (unopened.Length > 0)
            {
                lines.Add($"No recorded step opened: {string.Join(", ", unopened)} (a run keeps only its last steps)");
            }
        }

        return lines;
    }

    // The CLIs' input holds its cached part (see TokenCount).
    private static long Tokens(AgentRunRecord run) => (run.InputTokens ?? 0) + (run.OutputTokens ?? 0);

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : null;

    private static string Money(decimal cost) => cost.ToString("$0.00", CultureInfo.InvariantCulture);

    private static string Shortened(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";

    private static string Plural(int number, string one) => $"{number} {(number == 1 ? one : one + "s")}";
}
