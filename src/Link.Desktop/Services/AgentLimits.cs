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
    public IReadOnlyList<AgentLimit> For(AgentProviderKind provider) =>
        _limits.Where(limit => limit.Provider == provider && (limit.ResetsAt is not { } reset || reset > DateTimeOffset.Now))
            .OrderBy(limit => limit.Window == "5 h" ? 0 : 1)
            .ThenBy(limit => limit.Window, StringComparer.Ordinal)
            .ToList();

    public bool IsNear(AgentProviderKind provider) =>
        For(provider).Any(limit => limit.Reached || limit.UsedPercent >= NearPercent);

    /// <summary>When an agent that reached its limit can work again; null when it can now or Link does not know.</summary>
    public DateTimeOffset? ReachedUntil(AgentProviderKind provider) =>
        For(provider).Where(limit => limit.Reached).Select(limit => limit.ResetsAt).Max();

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
