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
    private const long PhaseShare = 50;

    /// <summary>
    /// How long a provider keeps a session's prompt cache after its last request: an hour for Claude Code on a
    /// subscription; at least half an hour for OpenAI's models from GPT-5.6 on, which Codex runs.
    /// </summary>
    public static TimeSpan CacheLasts(AgentProviderKind provider) => TimeSpan.FromMinutes(provider == AgentProviderKind.Codex ? 30 : 60);

    /// <summary>
    /// The reason to start fresh instead of resuming, in words for the task's history; null: resume. The session holds
    /// <paramref name="contextTokens"/> (null: not told). <paramref name="budget"/> is Settings' Context budget, capped
    /// by the model's <paramref name="window"/> (on Auto, 0, the window alone: see <see cref="AgentContext.Limit"/>). The
    /// session counts as too big at 80% of it; as cold when it was idle as long as its cache lasts
    /// (<paramref name="cacheLasts"/>, an hour when not told) and holds at least 100k and a quarter of a set budget.
    /// </summary>
    public static string? Why(long? contextTokens, int budget, TimeSpan idle, int? window = null, TimeSpan? cacheLasts = null)
    {
        if (contextTokens is not { } held) return null;
        var limit = AgentContext.Limit(budget, window);
        if (limit is { } near && held * 100 >= near * NearEnd)
        {
            return $"the session holds {AgentContext.Tokens(held)} of {AgentContext.Tokens(near)}: each step of the next run would re-send it; the task's state is far shorter";
        }

        if (held >= Math.Max(ColdFloor, budget > 0 ? (limit ?? 0) * ColdShare / 100 : 0) && idle >= (cacheLasts ?? CacheLasts(AgentProviderKind.ClaudeCode)))
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
    /// of the run's agent lasts (<see cref="CacheLasts"/>), so that a reply to a run just ended is never taken away from its session.
    /// </summary>
    public static string? Why(AgentRunRecord? last, bool autoCompact, int budget, DateTimeOffset now, bool afterLimit, IReadOnlyList<AgentPlanItem>? plan = null)
    {
        if (!autoCompact || last is null) return null;
        var idle = now - (last.StartedAt + last.Duration);
        var lasts = CacheLasts(last.Provider);
        var window = last.ContextWindow is { } told ? (int)Math.Min(told, int.MaxValue) : (int?)null;
        if (!afterLimit && idle < lasts) return PhaseEnd(last.ContextTokens, budget, window, plan);
        return Why(last.ContextTokens, budget, idle, window, lasts);
    }

    /// <summary>
    /// The reason to start the next part of a plan fresh: the run ended with a part of its plan done and more to come
    /// (none under way), and the session holds half its limit or more. The task's state carries the plan and what was
    /// done; the files and skills the done part read stay behind, which every step of the next part would re-send.
    /// </summary>
    public static string? PhaseEnd(long? contextTokens, int budget, int? window, IReadOnlyList<AgentPlanItem>? plan)
    {
        // A team's plan has its own sessions per step.
        if (contextTokens is not { } held || plan is null || plan.Any(item => item.Worker is not null) || AgentContext.Limit(budget, window) is not { } limit) return null;
        var done = plan.Count(item => item.Done);
        if (done == 0 || done == plan.Count || plan.Any(item => item.Active) || held * 100 < limit * PhaseShare) return null;
        return $"{done} of {plan.Count} steps of the plan are done and the session holds {AgentContext.Tokens(held)} of {AgentContext.Tokens(limit)}: the next part starts from the task's state";
    }

    /// <summary>
    /// The reason to start a step of Link's own, such as the team lead's review, in a fresh session rather than resume the
    /// session <paramref name="last"/> ran in: no reply of the user's is taken from it, so a session near its budget
    /// starts fresh at once. Null: resume, or Settings do not allow it.
    /// </summary>
    public static string? ForStep(AgentRunRecord? last, bool autoCompact, int budget, DateTimeOffset now) =>
        Why(last, autoCompact, budget, now, afterLimit: true);

    private static string Idle(TimeSpan idle) =>
        idle.TotalHours >= 48 ? $"{(int)idle.TotalDays} days"
        : idle.TotalMinutes >= 120 ? $"{(int)idle.TotalHours} h"
        : $"{(int)idle.TotalMinutes} min";
}
