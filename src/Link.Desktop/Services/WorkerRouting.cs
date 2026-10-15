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
    /// it is set up, else Claude Code; Light is Claude Code (Haiku), else Codex (its lightest model). The two
    /// subscriptions share the work; the custom model serves a tier only when the user chose it.
    /// </summary>
    public static IReadOnlyDictionary<WorkerTier, Worker> Workers(AgentSettings settings, Func<AgentProviderKind, WorkerState> state)
    {
        bool Set(AgentProviderKind provider) => state(provider) != WorkerState.NotSet;
        Worker Pick(WorkerTier tier, AgentLevel level, params AgentProviderKind[] preferred) =>
            settings.Team.For(tier) is { } chosen && Set(chosen.Provider)
                ? Of(settings, chosen.Provider, chosen.Level)
                : Of(settings, preferred.FirstOrDefault(Set, preferred[^1]), level);

        var lead = settings.DefaultProvider;
        return new Dictionary<WorkerTier, Worker>
        {
            [WorkerTier.Strong] = Pick(WorkerTier.Strong, AgentLevel.Deep, lead, AgentProviderKind.ClaudeCode),
            [WorkerTier.Standard] = Pick(WorkerTier.Standard, AgentLevel.Standard, AgentProviderKind.Codex, AgentProviderKind.ClaudeCode),
            [WorkerTier.Light] = Pick(WorkerTier.Light, AgentLevel.Light, AgentProviderKind.ClaudeCode, AgentProviderKind.Codex)
        };
    }

    /// <summary>The next tier up; null above Strong, where a failed step stops the team.</summary>
    public static WorkerTier? Escalate(WorkerTier tier) => tier < WorkerTier.Strong ? tier + 1 : null;

    /// <summary>
    /// The tier and worker for a step, and why. The tier is the one the plan asked for (<paramref name="asked"/>; an
    /// escalated step asks for the next tier up), else the kind's own. Design and art (<see cref="StepKinds.IsArt"/>)
    /// never go below Standard, and art goes only to an agent that sees images (<paramref name="seesImages"/>; default:
    /// all but the custom model). Quality first: a cheaper worker the project has proven for the kind is only suggested
    /// in the reason, unless Settings let Link lower the tier by itself (<see cref="TeamSettings.AutoLower"/>). A
    /// worker that cannot work now (not set up, at its limit) is replaced by the other subscription agent at the same
    /// level (Claude Code and Codex stand in for each other); the step rises a tier only where the worker failed the
    /// kind here, or neither can work. Above Strong there is no worker, and the reason says why.
    /// An agent the plan named (<paramref name="agent"/>, as the user asked: one agent against another) serves every tier
    /// at the level of the tier's own worker, and has no stand-in: another agent would not be the one asked for.
    /// <paramref name="agentName"/> names an agent in the reason (default <see cref="ShortName"/>).
    /// </summary>
    public static (WorkerTier Tier, Worker? Worker, string Reason) Route(
        StepKind kind,
        WorkerTier? asked,
        AgentSettings settings,
        Func<AgentProviderKind, WorkerState> state,
        WorkerEvidence evidence,
        Func<AgentProviderKind, string>? agentName = null,
        Func<AgentProviderKind, bool>? seesImages = null,
        AgentProviderKind? agent = null)
    {
        agentName ??= ShortName;
        seesImages ??= provider => provider != AgentProviderKind.Custom;
        var workers = Workers(settings, state);
        if (agent is { } named) workers = workers.ToDictionary(pair => pair.Key, pair => Of(settings, named, pair.Value.Level));
        var parts = new List<string>();
        string? hint = null;
        var floor = kind == StepKind.Design || StepKinds.IsArt(kind) ? WorkerTier.Standard : WorkerTier.Light;
        var tier = asked ?? StepKinds.TierOf(kind);
        var below = tier > floor ? workers[tier - 1] : null;
        var chosen = string.Join(' ', new[] { asked?.ToString(), agent is { } by ? agentName(by) : null }.OfType<string>());
        if (chosen.Length > 0) parts.Add($"the plan chose {chosen}");
        if (tier < floor)
        {
            parts.Add($"{kind} goes no lower than {floor}");
            tier = floor;
        }
        else if (asked is null && below is not null && Cannot(kind, below, state, seesImages, agentName) is null && evidence.Proven(kind, below))
        {
            var (done, failed) = evidence.Counts(kind, below);
            var proof = $"{agentName(below.Provider)} did {done} of {done + failed} here";
            if (settings.Team.AutoLower)
            {
                parts.Add(proof);
                tier--;
            }
            else
            {
                hint = $"{proof}: {tier - 1} could take it";
            }
        }

        while (true)
        {
            var worker = workers[tier];
            var away = Cannot(kind, worker, state, seesImages, agentName);
            var failedHere = away is null ? FailedHere(kind, worker, evidence, agentName) : null;
            if (away is null && failedHere is null) return (tier, worker, Reason(kind, parts, agentName(worker.Provider), hint));
            if (!parts.Contains(away ?? failedHere!)) parts.Add(away ?? failedHere!);
            if (away is not null && agent is null && StandIn(kind, worker, settings, state, evidence, seesImages, agentName, parts) is { } stand)
            {
                return (tier, stand, Reason(kind, parts, $"{agentName(stand.Provider)} ({tier})", hint));
            }

            if (Escalate(tier) is { } up)
            {
                tier = up;
                continue;
            }

            // Failing the kind is no reason to leave Strong without a worker: it stays, with the reason.
            return away is null
                ? (tier, worker, Reason(kind, parts, agentName(worker.Provider), hint))
                : (tier, null, $"{kind}: {string.Join(", ", parts)} → no agent can take it now");
        }
    }

    private static Worker Of(AgentSettings settings, AgentProviderKind provider, AgentLevel level) =>
        new(provider, level, settings.Level(provider, level).Model);

    /// <summary>
    /// The other subscription agent at the worker's level, if it can work now and has not failed the kind here; why each
    /// cannot joins <paramref name="parts"/>.
    /// </summary>
    private static Worker? StandIn(StepKind kind, Worker worker, AgentSettings settings, Func<AgentProviderKind, WorkerState> state,
        WorkerEvidence evidence, Func<AgentProviderKind, bool> seesImages, Func<AgentProviderKind, string> agentName, List<string> parts)
    {
        foreach (var other in new[] { AgentProviderKind.ClaudeCode, AgentProviderKind.Codex }.Where(provider => provider != worker.Provider))
        {
            var stand = Of(settings, other, worker.Level);
            if ((Cannot(kind, stand, state, seesImages, agentName) ?? FailedHere(kind, stand, evidence, agentName)) is not { } why) return stand;
            if (!parts.Contains(why)) parts.Add(why);
        }

        return null;
    }

    private static string Reason(StepKind kind, List<string> parts, string to, string? hint) =>
        (parts.Count == 0 ? $"{kind} → {to}" : $"{kind}: {string.Join(", ", parts)} → {to}") + (hint is null ? string.Empty : $" ({hint})");

    /// <summary>Why a worker cannot take a step now whatever it did before, in words: not set up, at its limit, no eyes for art; null if it can.</summary>
    private static string? Cannot(
        StepKind kind, Worker worker, Func<AgentProviderKind, WorkerState> state, Func<AgentProviderKind, bool> seesImages, Func<AgentProviderKind, string> agentName)
    {
        var name = agentName(worker.Provider);
        return state(worker.Provider) switch
        {
            WorkerState.NotSet => $"{name} is not set up",
            WorkerState.LimitReached => $"{name} limit reached",
            _ => StepKinds.IsArt(kind) && !seesImages(worker.Provider) ? $"{name} cannot see images" : null
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
