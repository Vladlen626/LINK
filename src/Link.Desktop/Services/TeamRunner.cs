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
    Finished
}

/// <summary>
/// The team's way through a task, without the UI: the lead's plan routed to workers, the step that runs next with the
/// brief its worker gets, and what follows a run of it: the next step, the user's OK, one more try, a tier up, or a stop.
/// Steps run one after another: they change one workspace and one editor.
/// </summary>
public static class TeamRunner
{
    /// <summary>Who takes a step of a kind, at the tier asked for (null: the kind's own), and why (see <see cref="WorkerRouting.Route"/>).</summary>
    public delegate (WorkerTier Tier, Worker? Worker, string Reason) Router(StepKind kind, WorkerTier? asked);

    /// <summary>A failure is told to the next worker in this many characters at most.</summary>
    private const int FailureLength = 600;

    /// <summary>
    /// Whether a brief is work for a team: it names two or more specialists, as a model that is then rigged, animated
    /// and put into the game does. A task for one specialist is done by one agent.
    /// </summary>
    public static bool Suggests(string brief, IReadOnlyList<AgentRole> roles) => ProjectRoles.SuggestAll(brief, roles).Count >= 2;

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
    /// counted. A step done hands over what its answer's last line says. A failed step gets one more try from the same
    /// worker, then goes a tier up; above Strong, or with no worker left, the team stops.
    /// </summary>
    public static TeamMove After(AgentTaskRecord record, TeamStep step, bool? done, string answer, string failure, Router route,
        Func<Worker, string> workerName, WorkerEvidence evidence)
    {
        if (done is null || step.Provider is not { } provider) return TeamMove.Paused;
        var worker = new Worker(provider, step.Level, step.Model);
        if (done.Value)
        {
            evidence.Record(step.Kind, worker, true);
            step.HandedOver = TeamPlan.HandedOver(answer) ?? step.HandedOver;
            step.Status = step.Check ? TeamStepStatus.AwaitsOk : TeamStepStatus.Done;
            return step.Check ? TeamMove.AwaitOk : Current(record) is null ? TeamMove.Finished : TeamMove.Next;
        }

        evidence.Record(step.Kind, worker, false);
        step.Failure = failure.Length <= FailureLength ? failure : failure[..FailureLength] + "…";
        step.Failures++;
        step.EscalatedFrom = step.Worker;
        if (step.Failures < 2) return TeamMove.Retry;
        if (WorkerRouting.Escalate(step.Tier) is not { } up)
        {
            step.Status = TeamStepStatus.Failed;
            return TeamMove.Failed;
        }

        Assign(step, route(step.Kind, up), workerName);
        step.Failures = 0;
        if (step.Provider is not null) return TeamMove.Escalated;
        step.Status = TeamStepStatus.Failed;
        return TeamMove.Failed;
    }

    /// <summary>The user's OK for a step that waited for it: done, and the team goes on.</summary>
    public static TeamMove Approve(AgentTaskRecord record, TeamStep step)
    {
        step.Status = TeamStepStatus.Done;
        return Current(record) is null ? TeamMove.Finished : TeamMove.Next;
    }

    /// <summary>
    /// The team plan as the task's plan card shows it: done, under way or waiting for the user's OK, and to do; each
    /// step with its worker (after an escalation "Haiku 5.5 → Codex") and its specialist's name.
    /// </summary>
    public static IReadOnlyList<AgentPlanItem> PlanItems(AgentTaskRecord record, Func<string?, string?> roleName) =>
        [.. record.Team.Select(step => new AgentPlanItem(
            step.Status == TeamStepStatus.Failed ? "Failed: " + step.Text : step.Text,
            step.Status == TeamStepStatus.Done,
            step.Status is TeamStepStatus.Running or TeamStepStatus.AwaitsOk,
            step.Status == TeamStepStatus.AwaitsOk ? "Waits for your OK: " + step.Text : null,
            Worker: step.EscalatedFrom is { } from && from != step.Worker ? $"{from} → {step.Worker}" : step.Worker,
            Role: roleName(step.Role)))];

    /// <summary>The team plan for the task's state: where each step stands and who does it.</summary>
    public static IEnumerable<string> StateLines(AgentTaskRecord record) =>
        record.Team.Select((step, index) =>
            $"- [{(step.Status == TeamStepStatus.Done ? "x" : " ")}] {index + 1}. {step.Text} ({step.Worker}" +
            (step.Status is TeamStepStatus.Failed or TeamStepStatus.AwaitsOk ? $", {step.Status}" : string.Empty) + ")");

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
