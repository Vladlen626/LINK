using System.Globalization;
using System.Text;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// A task as one Markdown text for a reader outside Link, such as ChatGPT in the browser: what it cost, what was asked
/// and given, the team's plan, then the thread in order, each run with its setup, numbers, last steps, files and answer.
/// Only what the task's record keeps: the sessions' own transcripts stay with the CLIs, and the user's words are as the
/// history keeps them (a long one cut).
/// </summary>
public static class TaskExport
{
    private const int FilesListed = 40;

    /// <summary>
    /// The task's Markdown. <paramref name="agentName"/> and <paramref name="roleName"/> give the names it shows (a null
    /// role is General); the tokens of <paramref name="strong"/>, the default agent, are counted apart as well.
    /// </summary>
    public static string Markdown(
        AgentTaskRecord task, Func<AgentProviderKind, string> agentName, Func<string?, string> roleName, AgentProviderKind strong, DateTimeOffset exported)
    {
        var text = new StringBuilder();
        text.Append("# LINK task: ").AppendLine(OneLine(task.Goal)).AppendLine();
        text.Append("Exported from LINK ").Append(Stamp(exported)).Append(" for analysis. Tokens \"in\" count all the input, its cached part among it; ")
            .AppendLine("\"≈\" weighs every token as fresh input at API price ratios (cached ×0.1, output ×5).").AppendLine();
        Summary(text, task, agentName, roleName, strong);
        Fenced(text, "Brief", task.Brief);
        for (var index = 0; index < task.Handovers.Count; index++) Fenced(text, $"Hand-over {index + 1}", task.Handovers[index]);
        Team(text, task, roleName);
        Plan(text, task);
        Training(text, task, agentName);
        Thread(text, task, agentName, roleName);
        // One line break everywhere, whatever the system's and the words' own.
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    /// <summary>A file name for the export: the task's start and its goal, without what Windows does not take in a name.</summary>
    public static string FileName(AgentTaskRecord task)
    {
        var goal = new string([.. OneLine(task.Goal).Select(each => char.IsLetterOrDigit(each) ? each : ' ')]);
        var words = string.Join('-', goal.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (words.Length > 50) words = words[..50].TrimEnd('-');
        return $"{task.CreatedAt.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)} {(words.Length > 0 ? words : "task")}.md";
    }

    private static void Summary(
        StringBuilder text, AgentTaskRecord task, Func<AgentProviderKind, string> agentName, Func<string?, string> roleName, AgentProviderKind strong)
    {
        var metric = TaskMetrics.Of(task, strong);
        text.AppendLine("## Summary");
        text.Append("- Status: ").Append(task.Status).Append(" · mode: ").Append(task.Mode).Append(" · specialist: ").AppendLine(roleName(task.RoleId));
        text.Append("- Folder: ").AppendLine(LastPart(task.Workspace));
        text.Append("- Started ").Append(Stamp(task.CreatedAt)).Append(", last change ").Append(Stamp(task.UpdatedAt))
            .Append("; the agents worked ").AppendLine(Duration(metric.Time));
        text.Append("- Runs: ").Append(metric.Runs);
        if (task.Runs.Count > 0)
        {
            text.Append(" (").AppendJoin(", ", task.Runs.GroupBy(run => run.Provider).Select(runs => $"{agentName(runs.Key)} {runs.Count()}")).Append(')');
        }

        text.Append(" · the user's words: ").Append(task.Entries.Count(entry => entry.Kind == AgentEntryKind.You))
            .Append(", ").Append(metric.Steers).Append(" of them while a run went");
        if (metric.Escalations > 0) text.Append(" · escalations: ").Append(metric.Escalations);
        text.AppendLine();
        foreach (var agent in metric.Agents)
        {
            var runs = task.Runs.Where(run => run.Provider == agent.Provider).ToArray();
            var cost = runs.Sum(run => run.CostUsd ?? 0);
            var spent = AgentLimits.SpentWords(agentName(agent.Provider), agent.Spent);
            text.Append("- ").Append(agentName(agent.Provider)).Append(": ")
                .Append(Tokens(agent.Tokens.Uncached + agent.Tokens.Cached, agent.Tokens.Cached, agent.Tokens.Output))
                .Append(", ≈ ").Append(AgentContext.Tokens((long)agent.Tokens.Equivalent))
                .Append(cost > 0 ? $", ${cost.ToString("0.00", CultureInfo.InvariantCulture)} at API prices" : string.Empty)
                .AppendLine(spent.Length > 0 ? "; " + spent : string.Empty);
        }

        if (metric.Agents.Count > 1)
        {
            text.Append("- In all ≈ ").Append(AgentContext.Tokens((long)metric.Equivalent)).Append(", ≈ ")
                .Append(AgentContext.Tokens((long)metric.StrongEquivalent)).Append(" of them on ").Append(agentName(strong)).AppendLine(", the default agent");
        }

        if (task.LastCheck is { } check) text.Append("- Check: ").AppendLine(OneLine(check.Split('\n')[0]));
        if (task.Skills.Count > 0) text.Append("- Skills to read first: ").AppendLine(string.Join(", ", task.Skills));
        if (task.Images.Count > 0) text.Append("- Images: ").AppendLine(string.Join(", ", task.Images.Select(LastPart)));
        if (task.ChangedFiles.Count > 0)
        {
            text.Append("- Files changed (").Append(task.ChangedFiles.Count).Append("): ").Append(string.Join(", ", task.ChangedFiles.Take(FilesListed)))
                .AppendLine(task.ChangedFiles.Count > FilesListed ? $" and {task.ChangedFiles.Count - FilesListed} more" : string.Empty);
        }

        text.AppendLine();
    }

    /// <summary>The team's plan as Link routed it: each step's kind, tier, specialist, worker and standing, and the notes that went with it.</summary>
    private static void Team(StringBuilder text, AgentTaskRecord task, Func<string?, string> roleName)
    {
        if (task.Team.Count == 0) return;
        text.AppendLine("## Team plan");
        for (var index = 0; index < task.Team.Count; index++)
        {
            var step = task.Team[index];
            text.Append(index + 1).Append(". ").Append(OneLine(step.Text)).Append(" — ").Append(step.Kind).Append(", ").Append(step.Tier)
                .Append(", ").Append(roleName(step.Role)).Append(", ").Append(step.Worker.Length > 0 ? step.Worker : "no worker").Append(": ").Append(step.Status);
            if (step.EscalatedFrom is { } from) text.Append(", escalated from ").Append(from);
            if (step.Failures > 0) text.Append(", failed ").Append(step.Failures).Append('×');
            if (step.Redos > 0) text.Append(", sent back ").Append(step.Redos).Append('×');
            if (step.Check) text.Append(", waits for the user's OK");
            if (step.Unverified) text.Append(", nothing shows it saved");
            text.AppendLine();
            Detail(text, "Why this worker", step.Reason);
            Detail(text, "The worker before ended in", step.Failure);
            Detail(text, "Handed over", step.HandedOver);
            Detail(text, "The lead's notes", step.ReviewNotes);
            Detail(text, "The art critic's notes", step.CriticNotes);
        }

        text.AppendLine();
    }

    /// <summary>The agent's own plan; a team's is the team plan above.</summary>
    private static void Plan(StringBuilder text, AgentTaskRecord task)
    {
        if (task.Team.Count > 0 || task.Plan.Count == 0) return;
        text.AppendLine("## The agent's plan");
        foreach (var item in task.Plan) text.Append(item.Done ? "- [x] " : "- [ ] ").AppendLine(OneLine(item.Text));
        text.AppendLine();
    }

    private static void Training(StringBuilder text, AgentTaskRecord task, Func<AgentProviderKind, string> agentName)
    {
        if (task.Training is not { } course) return;
        text.AppendLine("## Training");
        text.Append("- Topic: ").Append(OneLine(course.Topic)).Append(" · stage: ").Append(course.Stage).Append(" · sources: ").AppendLine(course.Sources.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var lesson in course.Lessons)
        {
            text.Append("- Lesson (").Append(lesson.Status).Append(", passed ").Append(lesson.Passed).Append(" of ").Append(lesson.Exams).Append(" exams, skill ")
                .Append(lesson.Skill).Append("): ").AppendLine(OneLine(lesson.Text));
        }

        foreach (var exam in course.Exams)
        {
            text.Append("- Exam ").Append(Stamp(exam.At)).Append(", ").Append(agentName(exam.Provider)).Append(exam.Model is { } model ? $" {model}" : string.Empty)
                .Append(": ").Append(exam.Result).Append(exam.Rating is { } rating ? $", the user's rating {rating}" : string.Empty)
                .AppendLine(exam.CriticScore is { } score ? $", the art critic's score {score}" : string.Empty);
            Detail(text, "The art critic's notes", exam.CriticNotes);
        }

        text.AppendLine();
    }

    /// <summary>The history in order: the user's words, each run (an earlier Link's runs have only their line) and the notes between.</summary>
    private static void Thread(StringBuilder text, AgentTaskRecord task, Func<AgentProviderKind, string> agentName, Func<string?, string> roleName)
    {
        text.AppendLine("## Thread").AppendLine();
        var runs = 0;
        AgentRunRecord? last = null;
        foreach (var entry in task.Entries)
        {
            switch (entry.Kind)
            {
                case AgentEntryKind.Brief or AgentEntryKind.You:
                    text.Append("**").Append(entry.Kind == AgentEntryKind.Brief ? "The task" : "You").Append("** · ").AppendLine(Stamp(entry.At));
                    Quoted(text, entry.Text);
                    break;
                case AgentEntryKind.Run when runs < task.Runs.Count:
                    last = task.Runs[runs++];
                    Run(text, last, runs, agentName, roleName);
                    break;
                // The run's answer is above; an earlier Link kept only these lines of it.
                case AgentEntryKind.Said when last is { Words.Length: > 0 }:
                    break;
                default:
                    text.Append("- ").Append(Stamp(entry.At)).Append(" · ").Append(entry.Kind).Append(": ").AppendLine(OneLine(entry.Text)).AppendLine();
                    break;
            }
        }

        while (runs < task.Runs.Count)
        {
            var run = task.Runs[runs++];
            Run(text, run, runs, agentName, roleName);
        }
    }

    private static void Run(StringBuilder text, AgentRunRecord run, int number, Func<AgentProviderKind, string> agentName, Func<string?, string> roleName)
    {
        text.Append("### Run ").Append(number).Append(" · ").Append(agentName(run.Provider)).Append(" · ").Append(run.Model ?? "default model");
        if (run.RanModel is { } ran) text.Append(" (").Append(ran).Append(')');
        text.Append(" · ").Append(run.Effort ?? "its own effort").Append(" · as ").Append(roleName(run.RoleId))
            .Append(" · ").Append(run.Judge ? "judge" : run.AutoApprove ? "auto" : "edits");
        if (run.Step is { } step) text.Append(" · team step ").Append(step).Append(run.Kind is { } kind ? $" ({kind}, {run.Tier})" : string.Empty);
        text.AppendLine();
        // The outcome tells the time: "Done in 3:12".
        text.Append("- ").Append(Stamp(run.StartedAt)).Append(" · ").Append(run.Outcome.Length > 0 ? run.Outcome : Duration(run.Duration));
        if (run.InputTokens is { } input) text.Append(" · ").Append(Tokens(input, run.CachedTokens ?? 0, run.OutputTokens ?? 0));
        if (run.ContextTokens is { } context) text.Append(" · the session held ").Append(AgentContext.Tokens(context)).Append(run.ContextWindow is { } window ? $" of {AgentContext.Tokens(window)}" : string.Empty);
        if (run.CostUsd is { } cost && cost > 0) text.Append(" · $").Append(cost.ToString("0.00", CultureInfo.InvariantCulture));
        if (AgentLimits.SpentWords(agentName(run.Provider), AgentLimits.Total(run.Spent)) is { Length: > 0 } spent) text.Append(" · ").Append(spent);
        if (run.Looped) text.Append(" · a loop was seen");
        text.AppendLine();
        if (run.EffortReason is { } why) text.Append("- Effort chosen by Link: ").AppendLine(OneLine(why));
        if (run.SessionId is null) text.AppendLine("- No session");
        if (run.Steps.Count > 0)
        {
            text.AppendLine("- Steps:");
            foreach (var each in run.Steps) text.Append("  - ").AppendLine(OneLine(each));
        }

        if (run.Files.Count > 0)
        {
            text.Append("- Files (").Append(run.Files.Count).Append("): ").Append(string.Join(", ", run.Files.Take(FilesListed)))
                .AppendLine(run.Files.Count > FilesListed ? $" and {run.Files.Count - FilesListed} more" : string.Empty);
        }

        text.AppendLine();
        if (run.Words.Trim().Length > 0) Quoted(text, run.Words);
    }

    /// <summary>A note of a step, on lines of its own under it.</summary>
    private static void Detail(StringBuilder text, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        text.Append("   - ").Append(label).Append(": ").AppendLine(value.Trim().Replace("\n", "\n     ", StringComparison.Ordinal));
    }

    /// <summary>A section with a text Link put together (a brief, a hand-over) as it was, in a fence longer than any inside it.</summary>
    private static void Fenced(StringBuilder text, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        var longest = 0;
        var row = 0;
        foreach (var each in body)
        {
            row = each == '`' ? row + 1 : 0;
            longest = Math.Max(longest, row);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        text.Append("## ").AppendLine(title).Append(fence).AppendLine("text").AppendLine(body.Trim('\r', '\n')).AppendLine(fence).AppendLine();
    }

    /// <summary>Words of the user or an agent as a quote, so that their own headings and lists stay theirs.</summary>
    private static void Quoted(StringBuilder text, string words)
    {
        foreach (var line in words.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) text.AppendLine(line.Length > 0 ? "> " + line : ">");
        text.AppendLine();
    }

    private static string Tokens(long input, long cached, long output) =>
        $"{AgentContext.Tokens(input)} in ({AgentContext.Tokens(cached)} cached), {AgentContext.Tokens(output)} out";

    private static string Duration(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // A folder or file by its name alone, on Windows paths too wherever the export is made.
    private static string LastPart(string path) => path.TrimEnd('\\', '/').Split('\\', '/')[^1];

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
