using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>A worker as Link runs and counts it: the agent, its level, and the model that level means (null: the CLI's own).</summary>
public sealed record Worker(AgentProviderKind Provider, AgentLevel Level, string? Model);

/// <summary>Whether Link may hand a step to an agent now.</summary>
public enum WorkerState
{
    Ready,

    /// <summary>Not installed, not signed in or not set up.</summary>
    NotSet,

    /// <summary>Its subscription's usage limit is reached (see <see cref="AgentLimits.ReachedUntil"/>).</summary>
    LimitReached
}

/// <summary>
/// Who does a step of a team plan: the worker of its tier, one tier lower where the project's evidence has proven the
/// cheaper worker for this kind, higher where the worker fails the kind here or cannot work now. Think expensive,
/// execute cheap, escalate only on failure; every choice comes with its reason in words.
/// </summary>
public static class WorkerRouting
{
    /// <summary>The agents' short names as the reasons and the panel use them; the custom model is named in Settings.</summary>
    public static string ShortName(AgentProviderKind provider) => provider switch
    {
        AgentProviderKind.ClaudeCode => "Claude",
        AgentProviderKind.Codex => "Codex",
        _ => "Custom"
    };

    /// <summary>
    /// The worker of each tier. A tier the user chose in Settings is served by that choice if its agent is set up; the
    /// others by default, only among agents that are set up: Strong is the default agent at Deep; Standard is Codex if
    /// it is set up, else Claude Code; Light is the custom model if set up, else Codex, else Claude Code.
    /// </summary>
    public static IReadOnlyDictionary<WorkerTier, Worker> Workers(AgentSettings settings, Func<AgentProviderKind, WorkerState> state)
    {
        bool Set(AgentProviderKind provider) => state(provider) != WorkerState.NotSet;
        Worker Of(AgentProviderKind provider, AgentLevel level) => new(provider, level, settings.Level(provider, level).Model);
        Worker Pick(WorkerTier tier, AgentLevel level, params AgentProviderKind[] preferred) =>
            settings.Team.For(tier) is { } chosen && Set(chosen.Provider)
                ? Of(chosen.Provider, chosen.Level)
                : Of(preferred.FirstOrDefault(Set, preferred[^1]), level);

        var lead = settings.DefaultProvider;
        return new Dictionary<WorkerTier, Worker>
        {
            [WorkerTier.Strong] = Pick(WorkerTier.Strong, AgentLevel.Deep, lead, AgentProviderKind.ClaudeCode),
            [WorkerTier.Standard] = Pick(WorkerTier.Standard, AgentLevel.Standard, AgentProviderKind.Codex, AgentProviderKind.ClaudeCode),
            [WorkerTier.Light] = Pick(WorkerTier.Light, AgentLevel.Light, AgentProviderKind.Custom, AgentProviderKind.Codex, AgentProviderKind.ClaudeCode)
        };
    }

    /// <summary>The next tier up; null above Strong, where a failed step stops the team.</summary>
    public static WorkerTier? Escalate(WorkerTier tier) => tier < WorkerTier.Strong ? tier + 1 : null;

    /// <summary>
    /// The tier and worker for a step, and why. The tier is the one the plan asked for (<paramref name="asked"/>; an
    /// escalated step asks for the next tier up), else the kind's own, lowered by one if the worker of the tier below is
    /// ready and proven for the kind here. Design and Asset never go below Standard, and an Asset step only to an agent
    /// that sees images (<paramref name="seesImages"/>; default: all but the custom model). Then it rises while the
    /// tier's worker is blocked for the kind here or cannot work now (not set up, at its limit). At Strong, a worker that
    /// cannot work is replaced by the other subscription agent at Deep (Claude Code and Codex stand in for each other);
    /// if neither can, the worker is null and the reason says so. <paramref name="agentName"/> names an agent in the
    /// reason (default <see cref="ShortName"/>).
    /// </summary>
    public static (WorkerTier Tier, Worker? Worker, string Reason) Route(
        StepKind kind,
        WorkerTier? asked,
        AgentSettings settings,
        Func<AgentProviderKind, WorkerState> state,
        WorkerEvidence evidence,
        Func<AgentProviderKind, string>? agentName = null,
        Func<AgentProviderKind, bool>? seesImages = null)
    {
        agentName ??= ShortName;
        seesImages ??= provider => provider != AgentProviderKind.Custom;
        var workers = Workers(settings, state);
        var parts = new List<string>();
        var floor = kind is StepKind.Design or StepKind.Asset ? WorkerTier.Standard : WorkerTier.Light;
        var tier = asked ?? StepKinds.TierOf(kind);
        var below = tier > floor ? workers[tier - 1] : null;
        if (asked is { } plan) parts.Add($"the plan chose {plan}");
        if (tier < floor)
        {
            parts.Add($"{kind} goes no lower than {floor}");
            tier = floor;
        }
        else if (asked is null && below is not null && Cannot(kind, below, state, seesImages, agentName) is null && evidence.Proven(kind, below))
        {
            var (done, failed) = evidence.Counts(kind, below);
            parts.Add($"{agentName(below.Provider)} did {done} of {done + failed} here");
            tier--;
        }

        while ((Cannot(kind, workers[tier], state, seesImages, agentName) ?? FailedHere(kind, workers[tier], evidence, agentName)) is { } problem)
        {
            parts.Add(problem);
            if (Escalate(tier) is not { } up) break;
            tier = up;
        }

        var worker = workers[tier];
        if (tier == WorkerTier.Strong && Cannot(kind, worker, state, seesImages, agentName) is not null)
        {
            foreach (var other in new[] { AgentProviderKind.ClaudeCode, AgentProviderKind.Codex }.Where(provider => provider != worker.Provider))
            {
                var stand = new Worker(other, AgentLevel.Deep, settings.Level(other, AgentLevel.Deep).Model);
                if ((Cannot(kind, stand, state, seesImages, agentName) ?? FailedHere(kind, stand, evidence, agentName)) is { } why)
                {
                    if (!parts.Contains(why)) parts.Add(why);
                    continue;
                }

                return (tier, stand, $"{kind}: {string.Join(", ", parts)} → {agentName(other)} (Strong)");
            }

            return (tier, null, $"{kind}: {string.Join(", ", parts)} → no agent can take it now");
        }

        return (tier, worker, parts.Count == 0 ? $"{kind} → {agentName(worker.Provider)}" : $"{kind}: {string.Join(", ", parts)} → {agentName(worker.Provider)}");
    }

    /// <summary>Why a worker cannot take a step now whatever it did before, in words: not set up, at its limit, no eyes for an Asset; null if it can.</summary>
    private static string? Cannot(
        StepKind kind, Worker worker, Func<AgentProviderKind, WorkerState> state, Func<AgentProviderKind, bool> seesImages, Func<AgentProviderKind, string> agentName)
    {
        var name = agentName(worker.Provider);
        return state(worker.Provider) switch
        {
            WorkerState.NotSet => $"{name} is not set up",
            WorkerState.LimitReached => $"{name} limit reached",
            _ => kind == StepKind.Asset && !seesImages(worker.Provider) ? $"{name} cannot see images" : null
        };
    }

    /// <summary>Why the project no longer gives a worker this kind of step, in words; null if it still does.</summary>
    private static string? FailedHere(StepKind kind, Worker worker, WorkerEvidence evidence, Func<AgentProviderKind, string> agentName)
    {
        if (!evidence.Blocked(kind, worker)) return null;
        var (done, failed) = evidence.Counts(kind, worker);
        return $"{agentName(worker.Provider)} failed it {failed} of {done + failed} here";
    }
}
