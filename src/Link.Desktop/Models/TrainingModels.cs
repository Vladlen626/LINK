namespace Link.Desktop.Models;

/// <summary>Where a training stands: the specialist studies, waits for the user's exam, sits it, or writes what it proved into skills.</summary>
public enum TrainingStage
{
    /// <summary>The specialist studies the sources; the user's words go on in its session until "Ready for exam".</summary>
    Learning = 0,

    /// <summary>The user's next words are an exam: a real task with references, in a new session.</summary>
    AwaitsExam = 1,

    /// <summary>The exam's session works or waits for the user's rating; words before it are a revision.</summary>
    Exam = 2,

    /// <summary>The exam passed: its session writes the lessons it proved into skills.</summary>
    Promoting = 3
}

public enum LessonStatus
{
    /// <summary>The specialist's own conclusion from the sources, not proven by practice: no worker is told it.</summary>
    Candidate = 0,

    /// <summary>Applied in an exam the user passed: written into a skill.</summary>
    Proven = 1
}

public enum ExamResult
{
    /// <summary>Not rated yet.</summary>
    Pending = 0,

    Pass = 1,
    Fail = 2,

    /// <summary>No file changed: a rating cannot prove what nothing shows.</summary>
    Unverified = 3
}

/// <summary>A short rule the specialist drew from the sources: the skill it goes into, and whether only this project needs it.</summary>
public sealed class TrainingLesson
{
    public string Text { get; set; } = string.Empty;

    /// <summary>The name of the skill the lesson goes into once proven, such as "blender-model"; empty: the specialist's main one.</summary>
    public string Skill { get; set; } = string.Empty;

    /// <summary>True: true of this game only, it goes into the project add-on skill rather than the pack's.</summary>
    public bool Project { get; set; }

    public LessonStatus Status { get; set; }

    /// <summary>Exams the lesson was applied in, and how many of them passed.</summary>
    public int Exams { get; set; }

    public int Passed { get; set; }
}

/// <summary>An exam: the user's task and references, who sat it, what it applied and saved, and how it was rated.</summary>
public sealed class TrainingExam
{
    /// <summary>The user's words that set the exam.</summary>
    public string Brief { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public AgentProviderKind Provider { get; set; }
    public string? Model { get; set; }

    /// <summary>The exam's session: a revision and the promotion go on in it.</summary>
    public string? SessionId { get; set; }

    /// <summary>The numbers of the lessons the exam applied, from 1, as the agent said.</summary>
    public List<int> Applied { get; set; } = [];

    /// <summary>The agent's own score of its result by the rubric, 1-5 per dimension: a critique before it hands in, not the verdict.</summary>
    public Dictionary<string, int> Scores { get; set; } = [];

    /// <summary>The review sheet and other frames the agent made, by path.</summary>
    public List<string> Sheet { get; set; } = [];

    /// <summary>The files the exam's runs changed, relative to the workspace: the evidence that it made something.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>The user's rating, 1-5; null until rated.</summary>
    public int? Rating { get; set; }

    public ExamResult Result { get; set; }
}

/// <summary>
/// A training of a specialist (the task's role): the topic and sources it studied, the lessons it drew, and its exams.
/// It lives on the task's record: the training is a task in the project's history, not a second memory.
/// </summary>
public sealed class TrainingCourse
{
    public string Topic { get; set; } = string.Empty;

    /// <summary>Links and files the user gave to study.</summary>
    public List<string> Sources { get; set; } = [];

    public List<TrainingLesson> Lessons { get; set; } = [];
    public List<TrainingExam> Exams { get; set; } = [];
    public TrainingStage Stage { get; set; }

    /// <summary>Auto on the card: the exams edit and run commands without asking, as a task of that card would.</summary>
    public bool AutoApprove { get; set; }
}
