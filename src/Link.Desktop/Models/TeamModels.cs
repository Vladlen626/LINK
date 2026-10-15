namespace Link.Desktop.Models;

/// <summary>
/// How much model a step of a team plan needs. A tier is served by one worker chosen in Settings (see
/// <see cref="TeamSettings"/>); the numbers go into saved plans and the evidence of workers.
/// </summary>
public enum WorkerTier
{
    /// <summary>Clear execution of a written step: renames, imports, docs, comments.</summary>
    Light = 0,

    /// <summary>Usual code and tests by a clear description.</summary>
    Standard = 1,

    /// <summary>Design, an unclear bug, taste, editor work, anything risky.</summary>
    Strong = 2
}

/// <summary>What a step of a team plan is, which settles its tier unless the plan says otherwise (see <see cref="Services.StepKinds"/>).</summary>
public enum StepKind
{
    Design = 0,
    Investigate = 1,
    Code = 2,
    Test = 3,
    Review = 4,
    Asset = 5,
    Mechanical = 6,
    Docs = 7,

    /// <summary>Art from the project's generator: seeds and a preset of a class it already makes, no modelling.</summary>
    Instance = 8,

    /// <summary>
    /// Never a step of a plan: the art critic's judgement, counted for the critic by whether the user's rating of an
    /// exam agreed with it (see <see cref="Services.ArtCritic"/>).
    /// </summary>
    Critique = 9
}

/// <summary>An agent at one of its levels: what a tier of the team is served by.</summary>
public sealed record WorkerChoice(AgentProviderKind Provider, AgentLevel Level);

/// <summary>
/// The team: whether the plan's steps go to workers and who serves each tier. A tier left null is served by the
/// default worker (see <see cref="Services.WorkerRouting.Workers"/>).
/// </summary>
public sealed class TeamSettings
{
    /// <summary>The task card offers "Auto — team", which hands the steps of a plan to workers.</summary>
    public bool Enabled { get; set; } = true;

    public WorkerChoice? Light { get; set; }
    public WorkerChoice? Standard { get; set; }
    public WorkerChoice? Strong { get; set; }

    /// <summary>An "Auto — team" task that would plan goes, by turns per kind, to the team or to the strong agent alone: to measure which is cheaper.</summary>
    public bool Alternate { get; set; }

    /// <summary>
    /// Off (quality first): a cheaper worker the project has proven for a kind of step is only suggested. On: Link
    /// gives it the step by itself.
    /// </summary>
    public bool AutoLower { get; set; }

    /// <summary>
    /// The lead reads the result of each step a cheaper worker did (its answer and diff) and accepts it, sends it back
    /// with notes, or gives it to a stronger worker.
    /// </summary>
    public bool LeadReview { get; set; } = true;

    /// <summary>
    /// An art critic, another agent than the artist's where one can, judges the frames of each art step of a team (an
    /// asset or an instance) and of each art exam before the user does (see <see cref="Services.ArtCritic"/>).
    /// </summary>
    public bool ArtReview { get; set; } = true;

    /// <summary>The worker chosen for a tier; null: the default one.</summary>
    public WorkerChoice? For(WorkerTier tier) => tier switch
    {
        WorkerTier.Light => Light,
        WorkerTier.Standard => Standard,
        _ => Strong
    };

    /// <summary>A choice of an agent or level this Link does not know is the default.</summary>
    public void Normalize()
    {
        Light = Known(Light);
        Standard = Known(Standard);
        Strong = Known(Strong);
    }

    public TeamSettings Clone() => new()
    {
        Enabled = Enabled, Light = Light, Standard = Standard, Strong = Strong, Alternate = Alternate, AutoLower = AutoLower, LeadReview = LeadReview,
        ArtReview = ArtReview
    };

    private static WorkerChoice? Known(WorkerChoice? choice) =>
        choice is not null && Enum.IsDefined(choice.Provider) && Enum.IsDefined(choice.Level) ? choice : null;
}

/// <summary>Who did a task: one agent the user chose or the strong agent alone, a team plan, or one worker that Auto routed the whole task to; or a training.</summary>
public enum TaskMode
{
    Solo = 0,
    Team = 1,
    Worker = 2,

    /// <summary>A specialist studies sources and proves what it learned in exams (see <see cref="TrainingCourse"/>).</summary>
    Training = 3
}

public enum TeamStepStatus
{
    Waiting = 0,
    Running = 1,
    Done = 2,
    Failed = 3,

    /// <summary>The worker is done and the step needs the user's eyes: the team waits for their OK.</summary>
    AwaitsOk = 4,

    /// <summary>The worker is done and the lead reads its result: its verdict decides the step.</summary>
    Reviewing = 5,

    /// <summary>The artist is done and the art critic judges its frames: its verdict decides the step.</summary>
    Critique = 6,

    /// <summary>The user skipped the step: the team is through it, and nothing counts for its worker.</summary>
    Skipped = 7
}

/// <summary>
/// A step of the task's team plan as Link routed it: kept on the task record, so that a reopened task shows who works
/// on what and a paused team can go on. Every property has a default, so that older records load.
/// </summary>
public sealed class TeamStep
{
    public string Text { get; set; } = string.Empty;
    public StepKind Kind { get; set; } = StepKind.Code;
    public WorkerTier Tier { get; set; } = WorkerTier.Standard;

    /// <summary>The tier the lead's plan asked for; null: the kind's own. The step is routed by it again.</summary>
    public WorkerTier? Asked { get; set; }

    /// <summary>The user chose the step's worker on the plan card: Link does not route it again, but a tier up after failures.</summary>
    public bool Pinned { get; set; }

    /// <summary>The user sent the step back to be done again: an earlier try's changes may be in the workspace.</summary>
    public bool Repeated { get; set; }

    /// <summary>The worker as the panel names it, such as "Haiku 5.5" or "Codex".</summary>
    public string Worker { get; set; } = string.Empty;

    /// <summary>The agent that works on the step and at which level; null when no agent could take it (see <see cref="Reason"/>).</summary>
    public AgentProviderKind? Provider { get; set; }

    public AgentLevel Level { get; set; } = AgentLevel.Standard;

    /// <summary>The agent the plan named for the step, as the user asked (one agent against another); null: the tier's own. A tier up keeps it.</summary>
    public AgentProviderKind? Agent { get; set; }

    /// <summary>The model of that level when the step was routed; null: the CLI's own. The evidence counts by it: Haiku and Opus are two workers.</summary>
    public string? Model { get; set; }

    /// <summary>Runs of the current worker on the step that failed: one more try, then the step goes a tier up.</summary>
    public int Failures { get; set; }

    public TeamStepStatus Status { get; set; }

    /// <summary>The worker that failed this step before the current one took it over; null if the step was not escalated.</summary>
    public string? EscalatedFrom { get; set; }

    /// <summary>What the worker before it ended in, told to the one that takes the step over.</summary>
    public string? Failure { get; set; }

    /// <summary>Why this worker: the routing decision in words.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Files or folders the plan names for the step.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>The specialist the step runs as, the file name of its role (<see cref="Services.ProjectRoles"/>); null: Link suggests one by the step's words.</summary>
    public string? Role { get; set; }

    /// <summary>Only the user can judge the result (a model's look, an animation's feel): the team pauses after the step for their OK.</summary>
    public bool Check { get; set; }

    /// <summary>What the worker handed over to the next step (paths, names of assets, what the next specialist needs); null until it said.</summary>
    public string? HandedOver { get; set; }

    /// <summary>The session of the worker's last run on the step: the lead's and the art critic's notes go back into it, where it knows what it did.</summary>
    public string? SessionId { get; set; }

    /// <summary>How many times the lead sent the step back (see <see cref="Services.TeamRunner.MaxRedos"/>).</summary>
    public int Redos { get; set; }

    /// <summary>The art critic's session on the step: after a redo it judges the new frames there, knowing what it asked for.</summary>
    public string? CriticSession { get; set; }

    /// <summary>What the lead asks the worker to change, until the worker's next run takes it; null: nothing.</summary>
    public string? ReviewNotes { get; set; }

    /// <summary>
    /// What the art critic said of the step's frames: the defects to fix, until the artist's next run takes them, or once
    /// the step waits for the user's OK what the user should look at; null: nothing.
    /// </summary>
    public string? CriticNotes { get; set; }

    /// <summary>
    /// An editor or DCC step its worker called done with no file changed: nothing shows it was saved, so it is not counted
    /// for the worker, and the user's OK lets the team go on.
    /// </summary>
    public bool Unverified { get; set; }
}
