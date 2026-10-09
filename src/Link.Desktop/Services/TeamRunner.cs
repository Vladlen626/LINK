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
    Stopped
}

/// <summary>
/// The team's way through a task, without the UI: the lead's plan routed to workers, the step that runs next with the
/// brief its worker gets, and what follows a run of it: the next step, the user's OK, one more try, a tier up, or a stop.
/// Steps run one after another: they change one workspace and one editor.
/// </summary>
public static partial class TeamRunner
{
    /// <summary>Who takes a step of a kind, at the tier asked for (null: the kind's own), and why (see <see cref="WorkerRouting.Route"/>).</summary>
    public delegate (WorkerTier Tier, Worker? Worker, string Reason) Router(StepKind kind, WorkerTier? asked);

    /// <summary>A failure is told to the next worker in this many characters at most.</summary>
    private const int FailureLength = 600;

    /// <summary>The lead sends a step back at most this many times; asked once more, a stronger worker takes it.</summary>
    public const int MaxRedos = 2;

    /// <summary>
    /// Whether a brief is work for a team: it names two or more specialists, as a model that is then rigged, animated
    /// and put into the game does, or lists three things to do or more. A task for one specialist is done by one agent.
    /// </summary>
    public static bool Suggests(string brief, IReadOnlyList<AgentRole> roles) =>
        ProjectRoles.SuggestAll(brief, roles).Count >= 2 || ListItem().Count(brief) >= 3;

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
            var routed = new TeamStep { Text = step.Text, Kind = step.Kind, Files = [.. step.Files], Role = step.Role, Check = step.Check };
            Assign(routed, route(step.Kind, step.Worker), workerName);
            return routed;
        })];

    /// <summary>The step the team is on: the first that is not done (under way, waiting for the user, failed, or next); null when all are done.</summary>
    public static TeamStep? Current(AgentTaskRecord record) => record.Team.FirstOrDefault(step => step.Status != TeamStepStatus.Done);

    /// <summary>The step's number as the plan shows it, from 1.</summary>
    public static int Number(AgentTaskRecord record, TeamStep step) => record.Team.IndexOf(step) + 1;

    /// <summary>
    /// What follows a run of the step (<paramref name="done"/>: true it did the step, false it failed it, null neither,
    /// as <see cref="WorkerEvidence.Verdict"/> reads a run), with the step brought up to date and the worker's evidence
    /// counted. A step done hands over what its answer's last line says, and goes to the lead's review when
    /// <paramref name="review"/> (see <see cref="Reviews"/>): its worker is counted by the verdict then. A failed step gets
    /// one more try from the same worker, then goes a tier up; above Strong, or with no worker left, the team stops.
    /// </summary>
    public static TeamMove After(AgentTaskRecord record, TeamStep step, bool? done, string answer, string failure, Router route,
        Func<Worker, string> workerName, WorkerEvidence evidence, bool review = false)
    {
        if (done is null || step.Provider is not { } provider) return TeamMove.Paused;
        var worker = new Worker(provider, step.Level, step.Model);
        if (done.Value)
        {
            step.HandedOver = TeamPlan.HandedOver(answer) ?? step.HandedOver;
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

    /// <summary>The user's OK for a step that waited for it: done, and the team goes on.</summary>
    public static TeamMove Approve(AgentTaskRecord record, TeamStep step)
    {
        step.Status = TeamStepStatus.Done;
        return Current(record) is null ? TeamMove.Finished : TeamMove.Next;
    }

    /// <summary>
    /// The team plan as the task's plan card shows it: done, under way or waiting for the user's OK, and to do; each
    /// step with its worker (after an escalation "Haiku 5.5 → Codex"), its specialist's name and why that worker.
    /// </summary>
    public static IReadOnlyList<AgentPlanItem> PlanItems(AgentTaskRecord record, Func<string?, string?> roleName) =>
        [.. record.Team.Select(step => new AgentPlanItem(
            step.Status == TeamStepStatus.Failed ? "Failed: " + step.Text : step.Text,
            step.Status == TeamStepStatus.Done,
            step.Status is TeamStepStatus.Running or TeamStepStatus.AwaitsOk or TeamStepStatus.Reviewing,
            step.Status switch
            {
                TeamStepStatus.AwaitsOk => "Waits for your OK: " + step.Text,
                TeamStepStatus.Reviewing => "The lead reviews: " + step.Text,
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
            (step.Status is TeamStepStatus.Failed or TeamStepStatus.AwaitsOk ? $", {step.Status}" : string.Empty) + ")");

    /// <summary>The step goes a tier up, to a new session of that tier's worker; with no tier or worker left it fails.</summary>
    private static TeamMove Up(TeamStep step, Router route, Func<Worker, string> workerName)
    {
        if (WorkerRouting.Escalate(step.Tier) is { } up)
        {
            Assign(step, route(step.Kind, up), workerName);
            step.Failures = 0;
            step.SessionId = null;
            if (step.Provider is not null) return TeamMove.Escalated;
        }

        step.Status = TeamStepStatus.Failed;
        return TeamMove.Failed;
    }

    private static string Short(string failure) => failure.Length <= FailureLength ? failure : failure[..FailureLength] + "…";

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
