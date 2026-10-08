using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// When Link starts a fresh session from the task's state rather than going on in the old one, because that is cheaper.
/// Every tool call of a run re-sends the whole session (cheap while its cache is warm, dear when cold); the task's
/// state is far shorter. A fresh session loses detail and rereads files, so only where the saving is clear.
/// </summary>
public static class CompactRule
{
    private const long NearEnd = 80;
    private const long ColdFloor = 100_000;
    private const long ColdShare = 25;
    private static readonly TimeSpan CacheLasts = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The reason to start fresh instead of resuming, in words for the task's history; null: resume. The session holds
    /// <paramref name="contextTokens"/> (null: not told). <paramref name="budget"/> is Settings' Context budget, capped
    /// by the model's <paramref name="window"/> where the custom model holds less. The session counts as too big at 80%
    /// of it; as cold when it was idle for an hour (the cache has expired) and holds at least 100k and a quarter of it.
    /// </summary>
    public static string? Why(long? contextTokens, int budget, TimeSpan idle, int? window = null)
    {
        if (contextTokens is not { } held) return null;
        var limit = Math.Min(budget, window ?? budget);
        if (held * 100 >= limit * NearEnd)
        {
            return $"the session holds {AgentContext.Tokens(held)} of {AgentContext.Tokens(limit)}: each step of the next run would re-send it; the task's state is far shorter";
        }

        if (held >= Math.Max(ColdFloor, limit * ColdShare / 100) && idle >= CacheLasts)
        {
            return $"the session's cache has expired ({Idle(idle)} idle): re-sending {AgentContext.Tokens(held)} would cost more than starting from the task's state";
        }

        return null;
    }

    /// <summary>
    /// The reason to start fresh when words are about to resume the task's <paramref name="last"/> run's session, from its
    /// own numbers: the context it left, the model's window and the time since it ended. Null: resume. Nothing when
    /// Settings do not allow it (<paramref name="autoCompact"/>). <paramref name="afterLimit"/>: the words come after the wait
    /// for a usage limit's reset, which is a wait by itself; a user's reply counts only after an idle as long as the cache
    /// lasts, so that a reply to a run just ended is never taken away from its session.
    /// </summary>
    public static string? Why(AgentRunRecord? last, bool autoCompact, int budget, DateTimeOffset now, bool afterLimit)
    {
        if (!autoCompact || last is null) return null;
        var idle = now - (last.StartedAt + last.Duration);
        if (!afterLimit && idle < CacheLasts) return null;
        return Why(last.ContextTokens, budget, idle, last.ContextWindow is { } window ? (int)Math.Min(window, int.MaxValue) : null);
    }

    private static string Idle(TimeSpan idle) =>
        idle.TotalHours >= 48 ? $"{(int)idle.TotalDays} days"
        : idle.TotalMinutes >= 120 ? $"{(int)idle.TotalHours} h"
        : $"{(int)idle.TotalMinutes} min";
}
