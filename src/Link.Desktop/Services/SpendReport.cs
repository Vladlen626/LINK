using System.Globalization;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>What a tip's button does: map the project, or compact the task into a fresh session.</summary>
public enum SpendFix
{
    None,
    Map,
    Compact
}

/// <summary>A tip on how the next run spends less: what the numbers say, why it costs, and what Link can do about it.</summary>
public sealed record SpendTip(SpendPart? Part, string Text, string Why, SpendFix Fix = SpendFix.None);

/// <summary>A run of a task as the Spend view lists it: which step or run, who worked, and where its tokens went.</summary>
public sealed record SpendRun(string Name, string Detail, SpendParts Parts, decimal? CostUsd);

/// <summary>A usage window of an agent: its share used, this task's part of it, and whether it lasts at this pace.</summary>
public sealed record SpendGauge(string Label, double Used, double Mine, string Note, bool Warn);

/// <summary>A day of the project's history: where its tokens went, its cost at API prices and its runs.</summary>
public sealed record SpendDay(DateOnly Day, SpendParts Parts, decimal? CostUsd, int Runs);

/// <summary>
/// What the Spend view shows of one task: where its tokens went (<see cref="Unsplit"/> ≈ tokens of runs from before the
/// split was kept), its cost and share of the 5-hour window, time and work, the last run's context curve, the runs one by
/// one, tips, and until when its cache stays warm.
/// </summary>
public sealed record SpendTask
{
    public required string Goal { get; init; }
    public SpendParts Parts { get; init; } = new();
    public double Unsplit { get; init; }
    public decimal? CostUsd { get; init; }
    public double? WindowPoints { get; init; }
    public TimeSpan Time { get; init; }
    public int Turns { get; init; }
    public int ToolCalls { get; init; }
    public int ToolErrors { get; init; }
    public IReadOnlyList<int> Curve { get; init; } = [];
    public string CurveLabel { get; init; } = string.Empty;

    /// <summary>The context budget and the model's window, in thousands of tokens; null where there is none or it is not told.</summary>
    public int? Budget { get; init; }

    public int? Window { get; init; }
    public IReadOnlyList<SpendRun> Runs { get; init; } = [];
    public IReadOnlyList<SpendTip> Tips { get; init; } = [];

    /// <summary>The workspace has a map already: a tip's fix updates it rather than writing one.</summary>
    public bool HasMap { get; init; }

    public DateTimeOffset? CacheWarmUntil { get; init; }

    /// <summary>What the session holds now: what a turn after the cache went cold reads again at full price.</summary>
    public long? ContextNow { get; init; }
}

/// <summary>
/// Turns a task's record, the project's days and the agents' windows into what the Spend view draws. Nothing here
/// reads a file or knows WPF.
/// </summary>
public static class SpendReport
{
    /// <summary>The longest history the view offers, the history file's length.</summary>
    public const int MostDays = UsageHistory.KeptDays;

    /// <summary>What a part is called in the legend and its tooltip.</summary>
    public static string Name(SpendPart part) => part switch
    {
        SpendPart.Brief => "Link's brief",
        SpendPart.Writing => "Model writing",
        SpendPart.Files => "File reads",
        SpendPart.Commands => "Commands & tools",
        SpendPart.Search => "Search",
        SpendPart.Images => "Pictures",
        _ => "CLI & earlier turns"
    };

    /// <summary>What a part holds, in a line for its tooltip.</summary>
    public static string Hint(SpendPart part) => part switch
    {
        SpendPart.Brief => "Rules, role, skills, map, the task's state and your words",
        SpendPart.Writing => "Answers, edits and thinking: output costs five times an input token",
        SpendPart.Files => "What reading files brought back",
        SpendPart.Commands => "Builds, tests, scripts, MCP calls and other tools",
        SpendPart.Search => "Grep, Glob, search commands and the web",
        SpendPart.Images => "Renders, screenshots and viewport shots the agent looked at",
        _ => "The CLI's own prompt and tool lists, MCP schemas, a resumed session's earlier turns"
    };

    /// <summary>
    /// A task's spend. <paramref name="who"/> names a run's agent and setup, <paramref name="budget"/> is the context
    /// budget (0: none), <paramref name="hasMap"/> whether the workspace has a map, <paramref name="canCompact"/> whether
    /// Compact is offered now.
    /// </summary>
    public static SpendTask Of(AgentTaskRecord task, Func<AgentRunRecord, string> who, int budget, bool hasMap, bool canCompact)
    {
        var split = task.Runs.Where(run => run.Spend is not null).ToArray();
        var parts = split.Aggregate(new SpendParts(), (sum, run) => sum + run.Spend!);
        var curved = task.Runs.LastOrDefault(run => run.ContextCurve.Count > 0);
        // A review runs in a session of its own: the task goes on in the last worker's.
        var last = task.Runs.LastOrDefault(run => !run.Judge);
        var minutes = last?.CacheMinutes ?? (last?.Provider == AgentProviderKind.ClaudeCode ? 5 : null);
        var team = task.Runs.Any(run => run.Step is not null);
        return new SpendTask
        {
            Goal = task.Goal,
            Parts = parts,
            Unsplit = task.Runs.Where(run => run.Spend is null).Sum(run => TokenCount.Of(run).Equivalent),
            CostUsd = task.Runs.Any(run => run.CostUsd is not null) ? task.Runs.Sum(run => run.CostUsd ?? 0) : null,
            WindowPoints = task.Runs.Any(run => run.Spent.Count > 0) ? AgentLimits.Total(task.Runs.SelectMany(run => run.Spent)).GetValueOrDefault("5 h") : null,
            Time = task.Runs.Aggregate(TimeSpan.Zero, (sum, run) => sum + run.Duration),
            Turns = task.Runs.Sum(run => run.Turns),
            ToolCalls = task.Runs.Sum(run => run.ToolCalls),
            ToolErrors = task.Runs.Sum(run => run.ToolErrors),
            Curve = curved?.ContextCurve ?? [],
            CurveLabel = curved is null ? string.Empty : $"{(team && curved.Step is { } step ? $"Step {step}" : "Last run")} · {Count(curved.ContextCurve.Count, "request")}",
            Budget = budget > 0 ? budget / 1000 : null,
            Window = curved?.ContextWindow is { } window ? (int)(window / 1000) : null,
            Runs = [.. task.Runs.Select((run, index) => new SpendRun(RunName(run, index, team), who(run), run.Spend ?? SpendMeter.Split(null, TokenCount.Of(run)), run.CostUsd))],
            Tips = Tips(task, parts, budget, hasMap, canCompact),
            HasMap = hasMap,
            CacheWarmUntil = last is null || minutes is null ? null : last.StartedAt + last.Duration + TimeSpan.FromMinutes(minutes.Value),
            ContextNow = last?.ContextTokens
        };
    }

    private static string RunName(AgentRunRecord run, int index, bool team) =>
        run.Judge ? "Review" : team && run.Step is { } step ? $"Step {step}{(run.Kind is { } kind ? " · " + kind : string.Empty)}" : $"Run {index + 1}";

    /// <summary>
    /// What stands out in a task's spend, the largest first, at most four: files read again and searching before the first
    /// edit (a map spares them), a cache that went cold, pictures, command output, thinking at a high effort, the CLI's
    /// own load, and a context near its budget.
    /// </summary>
    public static IReadOnlyList<SpendTip> Tips(AgentTaskRecord task, SpendParts parts, int budget, bool hasMap, bool canCompact)
    {
        var tips = new List<(double Size, SpendTip Tip)>();
        var total = Math.Max(1, parts.Total);
        string Share(SpendPart part) => $"{Math.Round(100.0 * parts[part] / total)}% (~{AgentContext.Tokens(parts[part])})";
        var mapWhy = hasMap
            ? "The project map seems to miss this: an update names what is where, so the next run goes straight there."
            : "A project map names what is where, so the next run goes straight there instead of looking around.";
        var mapFix = SpendFix.Map;

        var rereads = FileReads.Join(task.Runs.SelectMany(run => run.Rereads), 50);
        var again = rereads.Sum(read => (double)read.Tokens * (read.Times - 1) / read.Times);
        if (again >= 4_000)
        {
            var first = rereads[0];
            var more = rereads.Count > 1 ? $", and {Count(rereads.Count - 1, "other file")} more than once" : string.Empty;
            tips.Add((again, new SpendTip(SpendPart.Files, $"{Path.GetFileName(first.Path)} was read {first.Times} times{more}: ~{AgentContext.Tokens((long)again)} read again", mapWhy, mapFix)));
        }

        if (task.Runs.Max(run => run.SearchesBeforeChange) is >= 15 and var searches)
        {
            tips.Add((parts.Search, new SpendTip(SpendPart.Search, $"{searches} searches before the first edit", mapWhy, mapFix)));
        }

        if (task.Runs.Sum(run => run.ColdTokens ?? 0) is >= 20_000 and var cold)
        {
            var minutes = task.Runs.LastOrDefault(run => run.CacheMinutes is not null)?.CacheMinutes ?? 5;
            tips.Add((cold * 0.9, new SpendTip(null, $"The cache went cold: ~{AgentContext.Tokens(cold)} read again at full price",
                $"A pause of more than {minutes} minutes between turns loses the cache. Before a long break, Compact makes the next turn small.",
                canCompact ? SpendFix.Compact : SpendFix.None)));
        }

        if (parts.Images >= 20_000 && parts.Images >= 0.25 * total)
        {
            tips.Add((parts.Images, new SpendTip(SpendPart.Images, $"Pictures took {Share(SpendPart.Images)}",
                "Each picture stays in the context and is read again on every request: fewer views per check, or one sheet of several views, cost less.")));
        }

        if (parts.Commands >= 30_000 && parts.Commands >= 0.35 * total)
        {
            tips.Add((parts.Commands, new SpendTip(SpendPart.Commands, $"Command output took {Share(SpendPart.Commands)}",
                "Long build and test logs stay in the context: quiet switches and the end of the log are usually enough.")));
        }

        var worker = task.Runs.LastOrDefault(run => !run.Judge);
        var effort = worker?.Effort;
        if (effort is "high" or "xhigh" or "max" && parts.Writing >= 0.5 * total && parts.Writing >= 20_000)
        {
            tips.Add((parts.Writing * 0.5, new SpendTip(SpendPart.Writing, $"The model's writing took {Share(SpendPart.Writing)} at {effort} effort",
                "Thinking is output, five times the price of input: a lower effort suits work the model knows well.")));
        }

        if (parts.Base >= 30_000 && parts.Base >= 0.4 * total)
        {
            tips.Add((parts.Base * 0.5, new SpendTip(SpendPart.Base, $"The CLI's own load took {Share(SpendPart.Base)}",
                "Its prompt, MCP tool lists and the session's earlier turns ride along on every request: a fresh session from the task's state carries less.",
                canCompact ? SpendFix.Compact : SpendFix.None)));
        }

        if (budget > 0 && worker?.ContextTokens is { } context && context >= 0.8 * budget)
        {
            tips.Add((context, new SpendTip(null, $"The context reached {AgentContext.Tokens(context)} of the {AgentContext.Tokens(budget)} budget",
                "Every request reads it whole. Compact starts a fresh session from the task's state.", canCompact ? SpendFix.Compact : SpendFix.None)));
        }

        // One button per kind of fix: the largest tip keeps it.
        var shown = new List<SpendTip>();
        foreach (var (_, tip) in tips.OrderByDescending(each => each.Size).Take(4))
        {
            shown.Add(tip.Fix != SpendFix.None && shown.Any(other => other.Fix == tip.Fix) ? tip with { Fix = SpendFix.None } : tip);
        }

        return shown;
    }

    /// <summary>
    /// The agents' windows: how much is used, this task's points of each since the window began, and whether it lasts
    /// until its reset at the pace it was used so far (a window less than ten minutes old tells no pace). The agent is
    /// named where the windows are of both.
    /// </summary>
    public static IReadOnlyList<SpendGauge> Gauges(IReadOnlyList<AgentLimit> limits, AgentTaskRecord? task, DateTimeOffset now)
    {
        var gauges = new List<SpendGauge>();
        var both = limits.Select(limit => limit.Provider).Distinct().Count() > 1;
        foreach (var limit in limits)
        {
            var used = Math.Clamp(limit.UsedPercent ?? 0, 0, 100);
            var length = AgentLimits.Length(limit.Window);
            var start = limit.ResetsAt is { } reset ? reset - length : (DateTimeOffset?)null;
            var mine = task is null
                ? 0
                : task.Runs.Where(run => run.Provider == limit.Provider && (start is null || run.StartedAt >= start))
                    .SelectMany(run => run.Spent).Where(spent => spent.Window == limit.Window).Sum(spent => spent.Percent);
            var label = limit.Window == "5 h" ? "5-hour window" : limit.Window.StartsWith("week", StringComparison.Ordinal) ? "Week" : limit.Window;
            if (both) label = $"{WorkerRouting.ShortName(limit.Provider)} · {label}";
            var note = limit.ResetsAt is { } at ? $"resets {When(at, now)}" : string.Empty;
            var warn = limit.Reached;
            if (limit.Reached)
            {
                note = limit.ResetsAt is { } until ? $"reached, free again {When(until, now)}" : "reached";
            }
            else if (start is { } began && limit.ResetsAt is { } ends && now - began >= TimeSpan.FromMinutes(10) && used > 0)
            {
                var full = now + TimeSpan.FromHours((100 - used) / (used / (now - began).TotalHours));
                warn = full < ends;
                note = warn ? $"full {When(full, now)} at this pace, before the reset {When(ends, now)}" : $"lasts at this pace; resets {When(ends, now)}";
            }

            gauges.Add(new SpendGauge(label, used, Math.Min(Math.Round(mine, 1), used), note, warn));
        }

        return gauges;
    }

    /// <summary>The days from <paramref name="first"/> to <paramref name="last"/>, every one of them, the agents of each added up.</summary>
    public static IReadOnlyList<SpendDay> Days(IEnumerable<UsageDay> days, DateOnly first, DateOnly last)
    {
        var byDay = days.GroupBy(day => day.Day).ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<SpendDay>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var of = byDay.GetValueOrDefault(day) ?? [];
            result.Add(new SpendDay(day,
                of.Aggregate(new SpendParts(), (sum, each) => sum + (each.Spend.Total > 0 ? each.Spend : SpendMeter.Split(null, new TokenCount(each.Uncached, each.Cached, each.Output)))),
                of.Any(each => each.CostUsd is not null) ? of.Sum(each => each.CostUsd ?? 0) : null,
                of.Sum(each => each.Runs)));
        }

        return result;
    }

    /// <summary>A time near now in few words: "at 16:40" today, "on Tue 09:00" within a week, else "on Oct 4".</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        return local.Date == today ? "at " + local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.Date < today.AddDays(7) && local.Date > today ? "on " + local.ToString("ddd HH:mm", CultureInfo.InvariantCulture)
            : "on " + local.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    private static string Count(int count, string what) => $"{count} {what}{(count == 1 ? string.Empty : "s")}";
}
