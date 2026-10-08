using System.Globalization;
using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// The agents' subscription windows as their CLIs last reported them (Claude Code's rate_limit_event, the rate_limits
/// Codex writes to its session log), kept between starts: how much is used, when it resets, whether an agent is near
/// or at its limit. What the CLIs did not say, Link does not make up.
/// </summary>
public sealed class AgentLimits
{
    /// <summary>Near the limit: Link offers a way on before the agent stops.</summary>
    public const double NearPercent = 90;

    private readonly string _file;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly List<AgentLimit> _limits;

    public AgentLimits(string file)
    {
        _file = file;
        _limits = AtomicJsonFile.Read<List<AgentLimit>>(file, _json) ?? [];
    }

    public void Update(IEnumerable<AgentLimit> limits)
    {
        foreach (var limit in limits)
        {
            _limits.RemoveAll(old => old.Provider == limit.Provider && old.Window == limit.Window);
            _limits.Add(limit);
        }

        try
        {
            AtomicJsonFile.Write(_file, _limits, _json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The numbers come again with the next run.
        }
    }

    /// <summary>An agent's windows that still hold: one whose reset has passed is used up no more.</summary>
    public IReadOnlyList<AgentLimit> For(AgentProviderKind provider) => For(provider, DateTimeOffset.Now);

    internal IReadOnlyList<AgentLimit> For(AgentProviderKind provider, DateTimeOffset now) =>
        _limits.Where(limit => limit.Provider == provider && Holds(limit, now))
            .OrderBy(limit => limit.Window == "5 h" ? 0 : 1)
            .ThenBy(limit => limit.Window, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Whether a window still holds at <paramref name="now"/>: until its reset; one the CLI gave no reset for only for its
    /// own length after it was seen, or a reached window would stop the agent for good.
    /// </summary>
    internal static bool Holds(AgentLimit limit, DateTimeOffset now) =>
        limit.ResetsAt is { } reset ? reset > now : limit.SeenAt + Length(limit.Window) > now;

    /// <summary>How long a window lasts, by its name: "5 h" five hours, "week…" seven days; any other is taken as five hours.</summary>
    internal static TimeSpan Length(string window) =>
        window.StartsWith("week", StringComparison.Ordinal) ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5);

    public bool IsNear(AgentProviderKind provider) =>
        For(provider).Any(limit => limit.Reached || limit.UsedPercent >= NearPercent);

    /// <summary>When an agent that reached its limit can work again; null when it can now or Link does not know.</summary>
    public DateTimeOffset? ReachedUntil(AgentProviderKind provider) =>
        For(provider).Where(limit => limit.Reached).Select(limit => limit.ResetsAt).Max();

    /// <summary>
    /// The agent a new task, the map or a note starts on: <paramref name="preferred"/>, or <paramref name="other"/> only while
    /// the preferred one's limit is reached now (<c>Until</c> is its reset). Nothing is remembered from one task to the next.
    /// </summary>
    public (AgentProviderKind Provider, DateTimeOffset? Until) StartsOn(AgentProviderKind preferred, AgentProviderKind other) =>
        ReachedUntil(preferred) is { } until ? (other, until) : (preferred, null);

    // A snapshot older than this may no longer be the window's state: another app may have used the subscription since.
    private static readonly TimeSpan BaselineTrust = TimeSpan.FromMinutes(15);

    // Codex's reset is computed from seconds left, so the same reset differs by a little from one report to the next.
    private static readonly TimeSpan SameResetWithin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How much of each window a run used, in percentage points: the window as the run's end reported it
    /// (<paramref name="after"/>, seen at or after <paramref name="runStart"/>) less the same window at the start
    /// (<paramref name="before"/>), rounded to 0.1, never below 0. A window that reset during the run (another reset time),
    /// or that was not reported during it, is left out. A <paramref name="before"/> that is missing, has no number or is
    /// older than 15 minutes is not trusted: the window's first report during the run (<paramref name="firstInRun"/>) is
    /// the baseline then.
    /// </summary>
    public static IReadOnlyList<UsageSpent> Spent(
        IReadOnlyList<AgentLimit> before, IReadOnlyList<AgentLimit> after, DateTimeOffset runStart, IReadOnlyList<AgentLimit>? firstInRun = null)
    {
        var spent = new List<UsageSpent>();
        foreach (var end in after)
        {
            if (end.UsedPercent is not { } used || end.SeenAt < runStart) continue;
            var start = before.FirstOrDefault(each => each.Window == end.Window);
            if (start is not { UsedPercent: not null } || start.SeenAt < runStart - BaselineTrust)
            {
                start = firstInRun?.FirstOrDefault(each => each.Window == end.Window && each.SeenAt >= runStart);
            }

            if (start?.UsedPercent is not { } from || !SameReset(start.ResetsAt, end.ResetsAt)) continue;
            spent.Add(new UsageSpent(end.Window, Math.Round(Math.Max(0, used - from), 1, MidpointRounding.AwayFromZero)));
        }

        return spent;
    }

    private static bool SameReset(DateTimeOffset? first, DateTimeOffset? second) =>
        first is { } one && second is { } other ? (one - other).Duration() <= SameResetWithin : first is null && second is null;

    /// <summary>The runs' spent windows added up per window, the 5-hour one first.</summary>
    public static IReadOnlyDictionary<string, double> Total(IEnumerable<UsageSpent> spent) =>
        spent.GroupBy(each => each.Window)
            .OrderBy(group => group.Key == "5 h" ? 0 : 1)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Math.Round(group.Sum(each => each.Percent), 1, MidpointRounding.AwayFromZero));

    /// <summary>
    /// "used 4% of Codex 5 h, 1% of week": what a run or a task used of the windows, the agent named at the first; only
    /// windows that lost 0.1 point or more, and empty when none did.
    /// </summary>
    public static string SpentWords(string agent, IReadOnlyDictionary<string, double> spent)
    {
        var parts = spent.Where(each => each.Value >= 0.1)
            .Select((each, index) => $"{each.Value.ToString("0.#", CultureInfo.InvariantCulture)}% of {(index == 0 ? agent + " " : string.Empty)}{each.Key}")
            .ToArray();
        return parts.Length == 0 ? string.Empty : "used " + string.Join(", ", parts);
    }

    /// <summary>"5 h 82% (resets 15:45) · week 31% · as of 14:02"; empty when nothing is known.</summary>
    public string Describe(AgentProviderKind provider)
    {
        var windows = For(provider);
        if (windows.Count == 0) return string.Empty;
        var parts = windows.Select(limit =>
        {
            var used = limit.Reached ? "limit reached" : limit.UsedPercent is { } percent ? $"{percent:0}%" : "used";
            return limit.ResetsAt is { } reset ? $"{limit.Window} {used} (resets {Time(reset)})" : $"{limit.Window} {used}";
        });
        return string.Join(" · ", parts) + $" · as of {Time(windows.Max(limit => limit.SeenAt))}";
    }

    /// <summary>A time of today as 15:45, another day's as Mon 15:45.</summary>
    public static string Time(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A window's name as the user reads it: "5 h", "week"; null for one Link does not show.</summary>
    internal static string? WindowName(string window) => window switch
    {
        "five_hour" => "5 h",
        "seven_day" => "week",
        "seven_day_opus" => "week, Opus",
        "seven_day_sonnet" => "week, Sonnet",
        _ => null
    };

    /// <summary>A share as a percentage, whether the CLI gives 0.82 or 82.</summary>
    internal static double? Percent(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number)
            ? number <= 1 ? number * 100 : number
            : null;

    /// <summary>A Unix time in seconds or milliseconds.</summary>
    internal static DateTimeOffset? UnixTime(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) && number > 0
            ? number > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(number) : DateTimeOffset.FromUnixTimeSeconds(number)
            : null;
}
