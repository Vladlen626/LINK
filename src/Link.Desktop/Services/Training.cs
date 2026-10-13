using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// A specialist's training, without the UI: it studies the user's sources and draws candidate lessons, sits an exam
/// (a real task of the user's with references, in a new session that has the lessons but not the sources), and the
/// lessons a passed exam applied are proven and written into skills. Reading alone proves nothing: an exam that
/// changed no file cannot pass. The requests are markdown beside Link.exe, edited without a rebuild.
/// </summary>
public static partial class Training
{
    /// <summary>More lessons than this from one study are a summary of the source, not rules to prove.</summary>
    public const int MaxLessons = 8;

    /// <summary>The user's rating from which an exam passes, of 5.</summary>
    public const int PassRating = 4;

    /// <summary>What a study run may use without asking besides the project's read list: it reads the web.</summary>
    public static readonly IReadOnlyList<string> WebTools = ["WebFetch", "WebSearch"];

    /// <summary>The scales an exam is scored on when the specialist's role has no rubric of its own.</summary>
    public const string DefaultRubric =
        "- Technical: it works, imports and builds without warnings; transforms, names and files as the project keeps them.\n" +
        "- Fidelity: what the task and the references ask for, no more and no less.\n" +
        "- Result: how good it looks or plays to the user, judged from frames, not from the code.\n" +
        "- Engine-ready: in the engine, at the right scale, with materials and collision as the game needs them.";

    public static string LearnFile => Path.Combine(AppContext.BaseDirectory, "harness", "training-learn.md");
    public static string ExamFile => Path.Combine(AppContext.BaseDirectory, "harness", "training-exam.md");
    public static string PromoteFile => Path.Combine(AppContext.BaseDirectory, "harness", "training-promote.md");

    /// <summary>The links in the user's words and the files they attached: what the specialist studies.</summary>
    public static List<string> Sources(string goal, IReadOnlyList<string> files) =>
        [.. Url().Matches(goal).Select(match => match.Value.TrimEnd('.', ',', ';', ':', ')')).Concat(files).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>What a study run is told: the user's topic and sources, then the request (the text of <see cref="LearnFile"/>).</summary>
    public static string LearnPrompt(string brief, string request) =>
        $"{brief.Trim()}\n\n{AgentRules.WithoutNotes(request)}";

    /// <summary>
    /// What an exam's new session is told: the request (the text of <see cref="ExamFile"/>), the candidate lessons by
    /// their numbers, the rubric (the role's own, else <see cref="DefaultRubric"/>) and the user's task.
    /// </summary>
    public static string ExamPrompt(string request, TrainingCourse course, AgentRole? role, string task)
    {
        var text = new StringBuilder(AgentRules.WithoutNotes(request))
            .Append("\n\n## Lessons to prove\n")
            .AppendJoin('\n', course.Lessons
                .Select((lesson, index) => (lesson, index))
                .Where(each => each.lesson.Status == LessonStatus.Candidate)
                .Select(each => $"{each.index + 1}. {each.lesson.Text.Trim()}"))
            .Append("\n\n## Rubric\n")
            .Append(Rubric(role) is { Length: > 0 } own ? "Your role's rubric:\n" + own : DefaultRubric)
            .Append("\n\n## The exam\n").Append(task.Trim());
        return text.ToString();
    }

    /// <summary>What the exam's session is told once it passed: the lessons it proved, to write into skills (the text of <see cref="PromoteFile"/>).</summary>
    public static string PromotePrompt(string request, IReadOnlyList<TrainingLesson> proven) =>
        AgentRules.WithoutNotes(request) + "\n\n## Proven lessons\n" + string.Join('\n', proven.Select(lesson =>
            $"- [{(lesson.Skill.Length > 0 ? lesson.Skill : "your main skill")}{(lesson.Project ? ", this game only" : ", all games")}] {lesson.Text.Trim()}"));

    /// <summary>The scales of the role's <c>## Rubric</c> section, as written; empty when it has none.</summary>
    public static string Rubric(AgentRole? role)
    {
        if (role is null || RubricSection().Match(role.Body) is not { Success: true } match) return string.Empty;
        return match.Groups[1].Value.Trim();
    }

    /// <summary>
    /// The lessons of the answer's last link-lessons block, at most <see cref="MaxLessons"/>, as candidates; empty when it
    /// has none, the block does not read or holds no lesson with words.
    /// </summary>
    public static List<TrainingLesson> Lessons(string answer)
    {
        if (LessonsBlock().Matches(answer).LastOrDefault() is not { } match) return [];
        try
        {
            using var json = JsonDocument.Parse(match.Groups[1].Value);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lessons", out var lessons)) root = lessons;
            if (root.ValueKind != JsonValueKind.Array) return [];
            return
            [
                .. root.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.Object)
                    .Select(element => new TrainingLesson
                    {
                        Text = AgentQuestion.Text(element, "text")?.Trim() ?? string.Empty,
                        Skill = AgentQuestion.Text(element, "skill")?.Trim() ?? string.Empty,
                        Project = element.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.True
                    })
                    .Where(lesson => lesson.Text.Length > 0)
                    .Take(MaxLessons)
            ];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// What the answer's last link-exam block says of the exam: the numbers of the lessons applied, the agent's own
    /// scores (1-5) and the frames it made. False when there is no block or it does not read; the exam is left as it was.
    /// </summary>
    public static bool ReadExam(string answer, TrainingExam exam)
    {
        if (ExamBlock().Matches(answer).LastOrDefault() is not { } match) return false;
        try
        {
            using var json = JsonDocument.Parse(match.Groups[1].Value);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            exam.Applied = root.TryGetProperty("applied", out var applied) && applied.ValueKind == JsonValueKind.Array
                ? [.. applied.EnumerateArray().Where(each => each.ValueKind == JsonValueKind.Number && each.TryGetInt32(out _)).Select(each => each.GetInt32()).Distinct()]
                : [];
            exam.Scores = root.TryGetProperty("scores", out var scores) && scores.ValueKind == JsonValueKind.Object
                ? scores.EnumerateObject()
                    .Where(score => score.Value.ValueKind == JsonValueKind.Number && score.Value.TryGetInt32(out _))
                    .ToDictionary(score => score.Name, score => Math.Clamp(score.Value.GetInt32(), 1, 5))
                : [];
            exam.Sheet = root.TryGetProperty("sheet", out var sheet) && sheet.ValueKind == JsonValueKind.Array
                ? [.. sheet.EnumerateArray().Where(each => each.ValueKind == JsonValueKind.String).Select(each => each.GetString()!.Trim()).Where(path => path.Length > 0)]
                : [];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The user's rating of the exam under way. No file changed: unverified, whatever the rating. From
    /// <see cref="PassRating"/>: passed, and each lesson it applied counts a pass and is proven; below: failed, and the
    /// lessons stay candidates. Returns the lessons this exam proved; with any, the training goes on to write them into
    /// skills, else the user's words go on with the specialist until the next exam is set.
    /// </summary>
    public static IReadOnlyList<TrainingLesson> Rate(TrainingCourse course, TrainingExam exam, int rating)
    {
        exam.Rating = Math.Clamp(rating, 1, 5);
        exam.Result = exam.Files.Count == 0 ? ExamResult.Unverified : exam.Rating >= PassRating ? ExamResult.Pass : ExamResult.Fail;
        var proven = new List<TrainingLesson>();
        if (exam.Result != ExamResult.Unverified)
        {
            foreach (var lesson in Applied(course, exam))
            {
                lesson.Exams++;
                if (exam.Result != ExamResult.Pass) continue;
                lesson.Passed++;
                if (lesson.Status == LessonStatus.Proven) continue;
                lesson.Status = LessonStatus.Proven;
                proven.Add(lesson);
            }
        }

        course.Stage = proven.Count > 0 ? TrainingStage.Promoting : TrainingStage.BetweenExams;
        return proven;
    }

    /// <summary>The lessons an exam applied, by the numbers it gave; numbers out of range are none.</summary>
    public static IEnumerable<TrainingLesson> Applied(TrainingCourse course, TrainingExam exam) =>
        exam.Applied.Where(number => number >= 1 && number <= course.Lessons.Count).Select(number => course.Lessons[number - 1]);

    /// <summary>The exam under way or waiting for its rating; null when none is.</summary>
    public static TrainingExam? OpenExam(TrainingCourse course) =>
        course.Stage == TrainingStage.Exam && course.Exams.Count > 0 && course.Exams[^1].Result == ExamResult.Pending ? course.Exams[^1] : null;

    /// <summary>
    /// The lessons as the task's card shows them, numbered as the exams name them: ✓ proven, ● applied in the exam under
    /// way, ○ a candidate; each with the skill it goes into and how its exams went.
    /// </summary>
    public static IReadOnlyList<AgentPlanItem> PlanItems(TrainingCourse course)
    {
        var applied = OpenExam(course)?.Applied ?? [];
        return
        [
            .. course.Lessons.Select((lesson, index) => new AgentPlanItem(
                // "1)", not "1.": the card's line is the text up to its first full stop.
                $"{index + 1}) {lesson.Text.Trim()}\n\n{Standing(lesson)}",
                lesson.Status == LessonStatus.Proven,
                lesson.Status == LessonStatus.Candidate && applied.Contains(index + 1),
                Worker: (lesson.Skill.Length > 0 ? lesson.Skill : "main skill") + (lesson.Project ? " · this game" : string.Empty)))
        ];
    }

    /// <summary>A rated exam as a line of the task's thread: "Exam 2 rated 4 of 5: passed · proven: lessons 1, 3".</summary>
    public static string Verdict(TrainingCourse course, TrainingExam exam, IReadOnlyList<TrainingLesson> proven)
    {
        var number = course.Exams.IndexOf(exam) + 1;
        var head = $"Exam {number} rated {exam.Rating} of 5";
        return exam.Result switch
        {
            ExamResult.Unverified => $"{head}: unverified, it changed no file, so it proves nothing",
            ExamResult.Fail => $"{head}: failed · the lessons stay candidates",
            _ when proven.Count == 0 => $"{head}: passed · it applied no candidate lesson, so none is proven",
            _ => $"{head}: passed · proven: lesson{(proven.Count == 1 ? string.Empty : "s")} " +
                 string.Join(", ", proven.Select(lesson => course.Lessons.IndexOf(lesson) + 1))
        };
    }

    private static string Standing(TrainingLesson lesson)
    {
        var where = $"Goes into {(lesson.Skill.Length > 0 ? "skill " + lesson.Skill : "the specialist's main skill")}" +
                    (lesson.Project ? " of this game only." : ", for all games.");
        var exams = lesson.Exams == 0 ? " Not tried in an exam yet."
            : $" Applied in {lesson.Exams} exam{(lesson.Exams == 1 ? string.Empty : "s")}, passed {lesson.Passed}.";
        return (lesson.Status == LessonStatus.Proven ? "Proven. " : "A candidate: no worker is told it. ") + where + exams;
    }

    [GeneratedRegex(@"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"```link-lessons[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LessonsBlock();

    [GeneratedRegex(@"```link-exam[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ExamBlock();

    // The section up to the next heading of its level or above.
    [GeneratedRegex(@"^##[ \t]+Rubric[ \t]*\r?\n(.*?)(?=^#{1,2}[ \t]|\z)", RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RubricSection();
}
