using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>Where the team goes after a step's run.</summary>
public enum TeamMove
{
    /// <summary>The step is done: the next one starts.</summary>
    Next,

    /// <summary>The step is done and only the user can judge it: the team waits for their OK.</summary>
    AwaitOk,

    /// <summary>The step failed once: the same worker tries again in a new session, told why.</summary>
    Retry,

    /// <summary>The step failed twice: a worker a tier up takes it, told what the one before ended in.</summary>
    Escalated,

    /// <summary>No worker is left for the step: the team stops and the user decides.</summary>
    Failed,

    /// <summary>The run neither did nor failed the step (a Stop, a question, a limit): the step waits in its session.</summary>
    Paused,

    /// <summary>Every step is done.</summary>
    Finished,

    /// <summary>The worker is done: the lead reviews its result before the team goes on.</summary>
    Review,

    /// <summary>The lead sent the step back: the same worker fixes it in its session, told the notes.</summary>
    Redo,

    /// <summary>The lead stopped the team at the step: the user decides.</summary>
    Stopped,

    /// <summary>An editor step called done with no file changed: not counted for its worker, it waits for the user's OK.</summary>
    Unverified,

    /// <summary>The artist is done: the art critic judges its frames before the team goes on.</summary>
    Critique
}

/// <summary>
/// The team's way through a task, without the UI: the lead's plan routed to workers, the step that runs next with the
/// brief its worker gets, and what follows a run of it: the next step, the user's OK, one more try, a tier up, or a stop.
/// Steps run one after another: they change one workspace and one editor.
/// </summary>
public static partial class TeamRunner
{
    /// <summary>
    /// Who takes a step of a kind, at the tier asked for (null: the kind's own), by the agent the plan named (null: the
    /// tier's own), and why (see <see cref="WorkerRouting.Route"/>).
    /// </summary>
    public delegate (WorkerTier Tier, Worker? Worker, string Reason) Router(StepKind kind, WorkerTier? asked, AgentProviderKind? agent);

    /// <summary>A failure is told to the next worker in this many characters at most.</summary>
    private const int FailureLength = 600;

    /// <summary>The lead sends a step back at most this many times; asked once more, a stronger worker takes it.</summary>
    public const int MaxRedos = 2;

    /// <summary>
    /// So many things to do in a brief's list make it work for a team: with fewer, the lead's plan, the steps' briefs and
    /// the reviews cost more than the cheaper workers save (docs/TOKENS_PLAN.md), and one agent does it for less.
    /// </summary>
    private const int TeamSteps = 4;

    /// <summary>
    /// Whether a brief is work for a team: it names two or more specialists, as a model that is then rigged, animated
    /// and put into the game does, or lists <see cref="TeamSteps"/> things to do or more. A task for one specialist is
    /// done by one agent.
    /// </summary>
    public static bool Suggests(string brief, IReadOnlyList<AgentRole> roles) =>
        ProjectRoles.SuggestAll(brief, roles).Count >= 2 || ListItem().Count(brief) >= TeamSteps;

    /// <summary>
    /// Whether the lead reviews a step its worker has done: reviews are on, the lead's session is known, the worker is
    /// below Strong, it changed files, and the step is not one only the user can judge.
    /// </summary>
    public static bool Reviews(AgentTaskRecord record, TeamStep step, bool changedFiles, bool enabled) =>
        enabled && record.LeadSession is not null && changedFiles && !step.Check && step.Tier != WorkerTier.Strong;

    /// <summary>The lead's plan, each step routed to its worker with the reason; <paramref name="workerName"/> names a worker for the panel.</summary>
    public static List<TeamStep> Steps(IReadOnlyList<PlannedStep> planned, Router route, Func<Worker, string> workerName) =>
        [.. planned.Select(step =>
        {
            var routed = new TeamStep
            {
                Text = step.Text, Kind = step.Kind, Files = [.. step.Files], Role = step.Role, Check = step.Check, Agent = step.Agent, Asked = step.Worker
            };
            Assign(routed, route(step.Kind, step.Worker, step.Agent), workerName);
            return routed;
        })];

    /// <summary>
    /// The lead's new plan for the steps left: the steps the team is through before the current one stay, the rest give
    /// way to <paramref name="planned"/>. For a first plan, or one before the team ran, that is all of it.
    /// </summary>
    public static List<TeamStep> Replanned(AgentTaskRecord record, IReadOnlyList<PlannedStep> planned, Router route, Func<Worker, string> workerName) =>
        [.. record.Team.TakeWhile(Over), .. Steps(planned, route, workerName)];

    /// <summary>Whether the team is through the step: done, or skipped by the user.</summary>
    public static bool Over(TeamStep step) => step.Status is TeamStepStatus.Done or TeamStepStatus.Skipped;

    /// <summary>The step the team is on: the first it is not through (under way, waiting for the user, failed, or next); null when all are.</summary>
    public static TeamStep? Current(AgentTaskRecord record) => record.Team.FirstOrDefault(step => !Over(step));

    /// <summary>The step's number as the plan shows it, from 1.</summary>
    public static int Number(AgentTaskRecord record, TeamStep step) => record.Team.IndexOf(step) + 1;

    /// <summary>
    /// What follows a run of the step (<paramref name="done"/>: true it did the step, false it failed it, null neither,
    /// as <see cref="WorkerEvidence.Verdict"/> reads a run), with the step brought up to date and the worker's evidence
    /// counted. A step done hands over what its answer's last line says, and goes to the art critic when
    /// <paramref name="critique"/>, else to the lead's review when <paramref name="review"/> (see <see cref="Reviews"/>):
    /// its worker is counted by the verdict then. A failed step gets one more try from the same worker, then goes a tier
    /// up; above Strong, or with no worker left, the team stops. An art step (<see cref="StepKinds.IsArt"/>) done with no
    /// file <paramref name="changed"/> is unverified: a "done" with nothing saved proves nothing.
    /// </summary>
    public static TeamMove After(AgentTaskRecord record, TeamStep step, bool? done, string answer, string failure, Router route,
        Func<Worker, string> workerName, WorkerEvidence evidence, bool review = false, bool changed = true, bool critique = false)
    {
        if (done is null || step.Provider is not { } provider) return TeamMove.Paused;
        var worker = new Worker(provider, step.Level, step.Model);
        if (done.Value)
        {
            step.HandedOver = TeamPlan.HandedOver(answer) ?? step.HandedOver;
            if (StepKinds.IsArt(step.Kind) && !changed)
            {
                step.Unverified = true;
                step.Status = TeamStepStatus.AwaitsOk;
                return TeamMove.Unverified;
            }

            if (critique)
            {
                step.Status = TeamStepStatus.Critique;
                return TeamMove.Critique;
            }

            if (review)
            {
                step.Status = TeamStepStatus.Reviewing;
                return TeamMove.Review;
            }

            evidence.Record(step.Kind, worker, true);
            step.Status = step.Check ? TeamStepStatus.AwaitsOk : TeamStepStatus.Done;
            return step.Check ? TeamMove.AwaitOk : Current(record) is null ? TeamMove.Finished : TeamMove.Next;
        }

        evidence.Record(step.Kind, worker, false);
        step.Failure = Short(failure);
        step.Failures++;
        step.EscalatedFrom = step.Worker;
        return step.Failures < 2 ? TeamMove.Retry : Up(step, route, workerName);
    }

    /// <summary>
    /// What follows the lead's verdict on a step under review. Accept: done, the worker counted as good. Redo: the same
    /// worker fixes it with the notes, at most <see cref="MaxRedos"/> times, after that as Escalate. Escalate: a tier up,
    /// told the notes. Stop: the step stays under review and the user decides.
    /// </summary>
    public static TeamMove Reviewed(AgentTaskRecord record, TeamStep step, LeadReview review, Router route, Func<Worker, string> workerName,
        WorkerEvidence evidence)
    {
        if (review.Verdict == ReviewVerdict.Stop || step.Provider is not { } provider) return TeamMove.Stopped;
        var worker = new Worker(provider, step.Level, step.Model);
        evidence.Record(step.Kind, worker, review.Verdict == ReviewVerdict.Accept);
        switch (review.Verdict)
        {
            case ReviewVerdict.Accept:
                step.Status = TeamStepStatus.Done;
                step.ReviewNotes = null;
                return Current(record) is null ? TeamMove.Finished : TeamMove.Next;
            case ReviewVerdict.Redo when step.Redos < MaxRedos:
                step.Redos++;
                step.ReviewNotes = review.Notes;
                step.Status = TeamStepStatus.Running;
                return TeamMove.Redo;
            default:
                step.Failure = Short("the lead's review: " + review.Notes);
                step.ReviewNotes = null;
                step.EscalatedFrom = step.Worker;
                step.Status = TeamStepStatus.Running;
                return Up(step, route, workerName);
        }
    }

    /// <summary>
    /// What follows the art critic's verdict on a step it judged. Accept: done, or waiting for the user's OK where the
    /// step asks for it, its worker counted as good. Redo (and an escalation, which art does not need): the artist fixes
    /// the defects in its session, at most <see cref="MaxRedos"/> times; a redo is how art is made, not a failure of its
    /// worker. After that, and on a stop, the step waits for the user's OK with the critic's notes: the user is the last
    /// judge of a look.
    /// </summary>
    public static TeamMove Critiqued(AgentTaskRecord record, TeamStep step, LeadReview review, WorkerEvidence evidence)
    {
        if (review.Verdict == ReviewVerdict.Accept)
        {
            if (step.Provider is { } provider) evidence.Record(step.Kind, new Worker(provider, step.Level, step.Model), true);
            step.CriticNotes = step.Check && review.Notes.Length > 0 ? review.Notes : null;
            step.Status = step.Check ? TeamStepStatus.AwaitsOk : TeamStepStatus.Done;
            return step.Check ? TeamMove.AwaitOk : Current(record) is null ? TeamMove.Finished : TeamMove.Next;
        }

        step.CriticNotes = review.Notes.Length > 0 ? review.Notes : null;
        if (review.Verdict is ReviewVerdict.Redo or ReviewVerdict.Escalate && step.Redos < MaxRedos && step.CriticNotes is not null)
        {
            step.Redos++;
            step.Status = TeamStepStatus.Running;
            return TeamMove.Redo;
        }

        step.Status = TeamStepStatus.AwaitsOk;
        return TeamMove.Stopped;
    }

    /// <summary>The user's OK for a step that waited for it: done, and the team goes on.</summary>
    public static TeamMove Approve(AgentTaskRecord record, TeamStep step)
    {
        step.Status = TeamStepStatus.Done;
        return Current(record) is null ? TeamMove.Finished : TeamMove.Next;
    }

    /// <summary>
    /// The user chose the step's worker on the plan card: that agent at that level, kept when Link routes the plan again
    /// and on a tier up. Null: Link picks again, by the plan's tier and the kind. A failed step can run again then.
    /// </summary>
    public static void Choose(TeamStep step, Worker? worker, Router route, Func<Worker, string> workerName)
    {
        step.Pinned = worker is not null;
        step.Agent = worker?.Provider;
        if (worker is null)
        {
            Assign(step, route(step.Kind, step.Asked, null), workerName);
        }
        else
        {
            var tier = worker.Level switch { AgentLevel.Light => WorkerTier.Light, AgentLevel.Deep => WorkerTier.Strong, _ => WorkerTier.Standard };
            Assign(step, (tier, worker, $"{step.Kind}: you chose {workerName(worker)} ({worker.Level})"), workerName);
        }

        if (step.Status == TeamStepStatus.Failed) RunAgain(step);
    }

    /// <summary>
    /// The step waits to run again in a new session, from its first try: its tries, notes and hand-over are cleared, and
    /// a step that ran is marked <see cref="TeamStep.Repeated"/>, so that its worker looks at what the earlier try left.
    /// What a failed worker ended in stays for the next.
    /// </summary>
    public static void RunAgain(TeamStep step)
    {
        step.Repeated = step.Repeated || step.SessionId is not null || step.Status != TeamStepStatus.Waiting;
        step.Status = TeamStepStatus.Waiting;
        step.Failures = 0;
        step.Redos = 0;
        step.SessionId = null;
        step.CriticSession = null;
        step.ReviewNotes = null;
        step.CriticNotes = null;
        step.HandedOver = null;
        step.Unverified = false;
    }

    /// <summary>
    /// The user skips the step: the team is through it, nothing counts for its worker, and the next worker is told it was
    /// skipped (after a try, that its changes may be in the workspace).
    /// </summary>
    public static TeamMove Skip(AgentTaskRecord record, TeamStep step)
    {
        step.HandedOver = step.Repeated || step.SessionId is not null || step.Status != TeamStepStatus.Waiting
            ? "Skipped by the user after a try: its changes may be in the workspace"
            : "Skipped by the user: nothing was done";
        step.Status = TeamStepStatus.Skipped;
        step.ReviewNotes = null;
        step.CriticNotes = null;
        return Current(record) is null ? TeamMove.Finished : TeamMove.Next;
    }

    /// <summary>
    /// The user goes back to a step the team is through: it and every step after it that ran or was skipped wait again
    /// (see <see cref="RunAgain"/>), without what earlier failures told; the files stay as they are.
    /// </summary>
    public static void BackTo(AgentTaskRecord record, TeamStep step)
    {
        foreach (var each in record.Team.Skip(record.Team.IndexOf(step)).Where(each => each.Status != TeamStepStatus.Waiting || each.SessionId is not null))
        {
            var skipped = each.Status == TeamStepStatus.Skipped && each.SessionId is null && !each.Repeated;
            RunAgain(each);
            each.Repeated = !skipped;
            each.Failure = null;
            each.EscalatedFrom = null;
        }
    }

    /// <summary>The numbers of the later steps that name a file or folder of the step's: skipping it may leave them without what they need.</summary>
    public static IReadOnlyList<int> Needing(AgentTaskRecord record, TeamStep step)
    {
        static string Norm(string path) => path.Replace('\\', '/').Trim('/').ToLowerInvariant();
        bool Shares(string one, string other) =>
            one == other || one.StartsWith(other + "/", StringComparison.Ordinal) || other.StartsWith(one + "/", StringComparison.Ordinal);
        var own = step.Files.Select(Norm).Where(path => path.Length > 0).ToArray();
        var at = record.Team.IndexOf(step);
        return [.. record.Team.Select((each, index) => (each, index)).Skip(at + 1)
            .Where(later => later.each.Files.Select(Norm).Any(path => own.Any(mine => Shares(mine, path))))
            .Select(later => later.index + 1)];
    }

    /// <summary>
    /// Steps still waiting whose worker cannot work now (<paramref name="canWork"/>: not set up, at its limit) or that have
    /// none are routed again as the plan asked, so that a limit that has reset or a worker set up since takes them; a
    /// step whose worker the user chose keeps it. The steps that changed, with the worker each had.
    /// </summary>
    public static IReadOnlyList<(TeamStep Step, string Before)> Reroute(AgentTaskRecord record, Router route, Func<Worker, string> workerName,
        Func<AgentProviderKind, bool> canWork)
    {
        var changed = new List<(TeamStep, string)>();
        foreach (var step in record.Team.Where(step => step.Status == TeamStepStatus.Waiting && !step.Pinned && (step.Provider is not { } provider || !canWork(provider))))
        {
            var routed = route(step.Kind, step.Asked, step.Agent);
            if (routed.Worker is null || (routed.Worker.Provider == step.Provider && routed.Worker.Level == step.Level)) continue;
            var before = step.Worker;
            Assign(step, routed, workerName);
            changed.Add((step, before));
        }

        return changed;
    }

    /// <summary>
    /// The team plan as the task's plan card shows it: done, under way or waiting for the user's OK, and to do; each
    /// step with its worker (after an escalation "Haiku 5.5 → Codex"), its specialist's name and why that worker.
    /// </summary>
    public static IReadOnlyList<AgentPlanItem> PlanItems(AgentTaskRecord record, Func<string?, string?> roleName) =>
        [.. record.Team.Select(step => new AgentPlanItem(
            step.Status switch { TeamStepStatus.Failed => "Failed: ", TeamStepStatus.Skipped => "Skipped: ", _ => string.Empty } + step.Text,
            Over(step),
            step.Status is TeamStepStatus.Running or TeamStepStatus.AwaitsOk or TeamStepStatus.Reviewing or TeamStepStatus.Critique,
            step.Status switch
            {
                TeamStepStatus.AwaitsOk => (step.Unverified ? "Unverified, no file changed: waits for your OK: " : "Waits for your OK: ") + step.Text,
                TeamStepStatus.Reviewing => "The lead reviews: " + step.Text,
                TeamStepStatus.Critique => "The art critic judges: " + step.Text,
                _ => null
            },
            Worker: step.EscalatedFrom is { } from && from != step.Worker ? $"{from} → {step.Worker}" : step.Worker,
            Role: roleName(step.Role),
            Why: step.Reason))];

    /// <summary>The team plan for the task's state: where each step stands and who does it.</summary>
    public static IEnumerable<string> StateLines(AgentTaskRecord record) =>
        // A step by its first sentence: each worker gets its own step in full, the others' would only cost tokens.
        record.Team.Select((step, index) =>
            $"- [{(step.Status == TeamStepStatus.Done ? "x" : " ")}] {index + 1}. {TeamPlan.Headline(step.Text).Title} ({step.Worker}" +
            (step.Status is TeamStepStatus.Failed or TeamStepStatus.AwaitsOk or TeamStepStatus.Skipped ? $", {step.Status}" : string.Empty) + ")");

    /// <summary>The step goes a tier up, to a new session of that tier's worker; with no tier or worker left it fails.</summary>
    private static TeamMove Up(TeamStep step, Router route, Func<Worker, string> workerName)
    {
        if (WorkerRouting.Escalate(step.Tier) is { } up)
        {
            Assign(step, route(step.Kind, up, step.Agent), workerName);
            step.Failures = 0;
            step.SessionId = null;
            if (step.Provider is not null) return TeamMove.Escalated;
        }

        step.Status = TeamStepStatus.Failed;
        return TeamMove.Failed;
    }

    private static string Short(string failure) => failure.Length <= FailureLength ? failure : failure[..FailureLength] + "…";

    /// <summary>
    /// The earlier step whose work <paramref name="step"/> builds on: the latest one its words name ("the step-1 blockout",
    /// "step 2's atlas"); the one just before when they name none; null when they name only others, or it is the first.
    /// </summary>
    public static TeamStep? BuildsOn(AgentTaskRecord record, TeamStep step)
    {
        var at = record.Team.IndexOf(step);
        if (at < 1) return null;
        var named = StepNumber().Matches(step.Text).Select(match => int.Parse(match.Groups[1].Value)).Where(number => number >= 1 && number <= at).ToList();
        return named.Count == 0 ? record.Team[at - 1] : record.Team[named.Max() - 1];
    }

    // "step 1", "step-2", "Step 3's".
    [GeneratedRegex(@"\bstep[- ](\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex StepNumber();

    // A line of a list: "- model the car", "2. add the doors".
    [GeneratedRegex(@"^[ \t]*(?:[-*•]|\d+[.)])[ \t]+\S", RegexOptions.Multiline)]
    private static partial Regex ListItem();

    private static void Assign(TeamStep step, (WorkerTier Tier, Worker? Worker, string Reason) routed, Func<Worker, string> workerName)
    {
        step.Tier = routed.Tier;
        step.Reason = routed.Reason;
        step.Provider = routed.Worker?.Provider;
        step.Level = routed.Worker?.Level ?? AgentLevel.Standard;
        step.Model = routed.Worker?.Model;
        step.Worker = routed.Worker is { } worker ? workerName(worker) : "nobody";
    }
}
