using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// A specialist's training in the Studio: it studies the user's sources and draws candidate lessons; on "Ready for
/// exam" the user's next words are an exam, done by a new session of the specialist with the lessons and not the
/// sources; the user rates it, and the lessons a passed exam applied are written into skills in its session. Between
/// exams the user's words go on in the last session, never into a new exam by themselves.
/// The decisions are <see cref="Training"/>'s; this only starts runs and shows where the training stands.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// A new training: the card's agent studies as the card's specialist with the card's access and the web to read: a
    /// read-only study could not fetch a page another way or do what the user asked of it on the way. The course keeps
    /// the topic, the sources and the card's Auto for the exams.
    /// </summary>
    private static async Task<(AgentTask Study, TrainingCourse Course)> TrainingStartAsync(AgentTask study, AgentPlan plan, LinkProject project,
        IReadOnlyList<string> files)
    {
        var request = File.Exists(Training.LearnFile) ? await File.ReadAllTextAsync(Training.LearnFile) : string.Empty;
        var course = new TrainingCourse
        {
            Topic = Shorten(plan.Goal.Split('\n')[0].Trim(), 200),
            Sources = Training.Sources(plan.Goal, files),
            AutoApprove = plan.AutoApprove
        };
        return (study with
        {
            Prompt = Training.LearnPrompt(study.Prompt, request),
            PlanOnly = false,
            AllowedTools = [.. AgentCoordinator.Tools(project, plan.Workspace).Concat(Training.WebTools).Distinct(StringComparer.Ordinal)],
            CheckCommand = null
        }, course);
    }

    /// <summary>
    /// After a run of a training: a study's lessons become the candidates and the exam is offered; an exam's answer tells
    /// what it applied and made, the art critic judges its frames where it made art, and the user is asked to rate it; a
    /// promotion done leaves the training waiting for the next exam. A run that asked something or was cut short waits
    /// for the user's words in its session.
    /// </summary>
    private void AfterTrainingRun(TaskWork work, AgentRun run, bool needsYou)
    {
        var record = work.Record;
        if (record.Training is not { } course) return;
        switch (course.Stage)
        {
            case TrainingStage.Learning:
                var lessons = Training.Lessons(run.Result.Summary);
                if (lessons.Count > 0)
                {
                    course.Lessons = lessons;
                    record.Add(AgentEntryKind.HandOff, $"Candidate lessons: {lessons.Count}, in Lessons above · no worker is told them before an exam proves them");
                    SaveTask(work);
                    ShowTask(work);
                }

                if (!needsYou && course.Lessons.Count > 0) OfferExam(work, course);
                break;
            case TrainingStage.Exam when Training.OpenExam(course) is { } exam:
                if (ArtCritic.Judges(run.Task))
                {
                    AfterExamCritique(work, course, exam, run, needsYou);
                    break;
                }

                exam.SessionId = AgentCoordinator.SessionOf(run) ?? exam.SessionId;
                Training.ReadExam(run.Result.Summary, exam);
                exam.Files = [.. exam.Files.Union(run.Changes.Select(change => Relative(change.Path, record.Workspace)), StringComparer.OrdinalIgnoreCase)];
                // Only a run that ended well and made something is judged again: a talk, a Stop or a failure (an API
                // error) leaves the critic's last score, and does not spend another critic's run.
                var remade = run.Changes.Count > 0 && run.Result is { Succeeded: true, Stopped: false };
                // A score of the work before this run says nothing of it now.
                if (remade) (exam.CriticScore, exam.CriticNotes, exam.CriticProvider, exam.CriticModel) = (null, null, null, null);
                SaveTask(work);
                ShowTask(work);
                if (!needsYou && remade) CritiqueExam(work, course, exam, run);
                break;
            case TrainingStage.Promoting when !needsYou && WorkerEvidence.Verdict(record.LastRun!) == true:
                course.Stage = TrainingStage.BetweenExams;
                var left = course.Lessons.Count(lesson => lesson.Status == LessonStatus.Candidate);
                record.Add(AgentEntryKind.HandOff, "The proven lessons are written into the skills · " + (left > 0
                    ? $"{Plural(left, "candidate lesson")} wait for an exam"
                    : "every lesson is proven: Finish the training"));
                SaveTask(work);
                ShowTask(work);
                OfferExam(work, course);
                break;
            case TrainingStage.BetweenExams when !needsYou:
                OfferExam(work, course);
                break;
        }
    }

    /// <summary>
    /// The art critic judges an exam's frames before the user rates them, where the exam made art the critic judges
    /// (<see cref="ArtCritic.Judgeable"/>) and saved it; the thread says why when no critic can.
    /// </summary>
    private void CritiqueExam(TaskWork work, TrainingCourse course, TrainingExam exam, AgentRun run)
    {
        if (!_settings.Current.Agent.Team.ArtReview || !ArtCritic.Judgeable(run.Task) || exam.Files.Count == 0) return;
        var record = work.Record;
        var number = course.Exams.IndexOf(exam) + 1;
        var frames = ArtCritic.Frames(exam.Sheet, record.Workspace, File.Exists);
        var (critic, why) = frames.Count == 0
            ? (null, "it listed no frames in its sheet")
            : CriticFor(new Worker(run.Task.Provider, AgentLevel.Deep, run.Task.Model), _projects.ReadWorkerEvidence(work.Project));
        record.Add(AgentEntryKind.HandOff, critic is null
            ? $"Exam {number}: no art critic judged it · {why}"
            : $"Exam {number}: {WorkerLabel(critic)} judges its frames before you rate it, blind to its answer · {why}");
        SaveTask(work);
        ShowTask(work);
        if (critic is not null) Dispatcher.BeginInvoke(() => _ = StartCriticAsync(work, critic, run, ExamAsked(record, exam), null, exam.Files, exam.Images, frames));
    }

    /// <summary>
    /// What the exam asks now: its brief and the user's words to it since. A critic told the brief alone once judged a
    /// Matiz against the wide car the user had replaced with it.
    /// </summary>
    private static string ExamAsked(AgentTaskRecord record, TrainingExam exam)
    {
        var later = record.Entries
            .Where(entry => entry.Kind == AgentEntryKind.You && entry.At > exam.At.AddSeconds(5) && !entry.Text.StartsWith("Rated exam", StringComparison.Ordinal))
            .Select(entry => "- " + entry.Text.Replace("(steer now) ", string.Empty, StringComparison.Ordinal))
            .ToArray();
        return later.Length == 0 ? exam.Brief : $"{exam.Brief}\n\nThe user's words since, the later ones count:\n{string.Join('\n', later)}";
    }

    /// <summary>
    /// The art critic's run on an exam ended: its score and defects go beside the rating, and the user's words after it go
    /// on in the exam's session, after a Stop or a limit too. Only a question of the critic's waits for the user's answer
    /// in its session.
    /// </summary>
    private void AfterExamCritique(TaskWork work, TrainingCourse course, TrainingExam exam, AgentRun run, bool needsYou)
    {
        if (work.Question is not null) return;
        if (work.Critiqued is { } judged) work.LastRun = judged;
        work.Critiqued = null;
        if (needsYou) return;
        var record = work.Record;
        var number = course.Exams.IndexOf(exam) + 1;
        var critic = WorkerLabel(new Worker(run.Task.Provider, AgentLevel.Deep, run.Task.Model));
        if (TeamPlan.SplitReview(run.Result.Summary).Review is { } review)
        {
            (exam.CriticScore, exam.CriticNotes, exam.CriticProvider, exam.CriticModel) =
                (review.Score, review.Notes.Length > 0 ? review.Notes : null, run.Task.Provider, run.Task.Model);
            record.Add(AgentEntryKind.HandOff, $"Exam {number}: {critic} " + (review.Score is { } score ? $"scores it {score} of 5" : "gave no score") +
                                               (review.Notes.Length > 0 ? " · " + Shorten(review.Notes, 200) : string.Empty));
        }
        else
        {
            record.Add(AgentEntryKind.HandOff, $"Exam {number}: {critic} gave no verdict");
        }

        SaveTask(work);
        ShowTask(work);
    }

    /// <summary>
    /// The study has drawn its lessons, or an exam is done with: words go on with the specialist, "Ready for exam" makes
    /// the next words the exam. Nothing is offered once no lesson is left to prove.
    /// </summary>
    private void OfferExam(TaskWork work, TrainingCourse course)
    {
        var left = course.Lessons.Count(lesson => lesson.Status == LessonStatus.Candidate);
        if (left == 0) return;
        OfferChoice(work,
            (course.Exams.Count == 0
                ? $"{Plural(left, "candidate lesson")} in Lessons above. Talk them over with the specialist below, or set the exam: "
                : $"{Plural(left, "candidate lesson")} left. Your words go on with the specialist (talk the exam over, fix the work, improve its skills), or set the next exam: ") +
            "a real task of this project with references, done by a new session that has these lessons and not the sources.",
            "Ready for exam",
            canRemember: false,
            () =>
            {
                course.Stage = TrainingStage.AwaitsExam;
                work.Record.Add(AgentEntryKind.You, "Ready for exam");
                SaveTask(work);
                ShowTask(work);
            });
    }

    /// <summary>
    /// The user's words set the exam: a new session of the training's agent as its specialist, with edits, told the
    /// candidate lessons, the rubric and the task; the user's images go along as the references.
    /// </summary>
    private async Task StartExamAsync(TaskWork work, TrainingCourse course, string words, IReadOnlyList<string> images)
    {
        var record = work.Record;
        var project = work.Project;
        var learner = record.Runs.FirstOrDefault();
        var provider = learner?.Provider ?? StartProvider;
        var request = File.Exists(Training.ExamFile) ? await File.ReadAllTextAsync(Training.ExamFile) : string.Empty;
        if (work.Busy || work.Waiting is not null || !_works.Contains(work)) return;
        var exam = new TrainingExam { Brief = words, Provider = provider, Model = learner?.Model, Images = [.. images] };
        course.Exams.Add(exam);
        course.Stage = TrainingStage.Exam;
        var candidates = course.Lessons.Count(lesson => lesson.Status == LessonStatus.Candidate);
        record.Add(AgentEntryKind.You, words + ImagesNote(images));
        record.Add(AgentEntryKind.HandOff, $"Exam {course.Exams.Count}: a new session of {AgentName(provider)} with {Plural(candidates, "candidate lesson")}, not the sources");
        SaveTask(work);
        var task = new AgentTask
        {
            Provider = provider,
            Workspace = record.Workspace,
            Prompt = Training.ExamPrompt(request, course, ProjectRoles.Find(record.RoleId, record.Workspace), words) +
                     (project.CheckCommand is { } check ? "\n\n" + AgentCoordinator.CheckNote(check) : string.Empty),
            Images = images,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace),
            CheckCommand = project.CheckCommand,
            AutoApprove = course.AutoApprove,
            Model = learner?.Model,
            Effort = learner?.Effort,
            RoleId = record.RoleId,
            Skills = record.Skills
        };
        work.Origin = task;
        await StartAgentAsync(work, task, null, phase: EffortPhase.New);
    }

    /// <summary>
    /// The exam waits for the user's rating: what it applied, what it made, its own scores and the art critic's, and why
    /// a pass would prove nothing when it changed no file or named no lesson. Words instead go to its session: a revision.
    /// </summary>
    private void ShowRating(AgentTaskRecord record, TrainingCourse course, TrainingExam exam)
    {
        var number = course.Exams.IndexOf(exam) + 1;
        var scores = exam.Scores.Count == 0 ? string.Empty
            : " Its own scores: " + string.Join(", ", exam.Scores.Select(score => $"{score.Key} {score.Value}")) + ".";
        if (exam is { CriticScore: { } critic, CriticProvider: { } judge })
        {
            scores += $" The art critic ({WorkerLabel(new Worker(judge, AgentLevel.Deep, exam.CriticModel))}): {critic} of 5" +
                      (exam.CriticNotes is { Length: > 0 } notes ? $", {Shorten(notes, 300)}" : ".");
        }

        var why = exam.Files.Count == 0 ? $" It changed no file: unverified, a pass cannot prove anything. Tell it to save its work first."
            : exam.Applied.Count == 0 ? " It named no lesson it applied: a pass proves none. Ask it below which it applied."
            : $" 4-5 passes it and proves lesson{(exam.Applied.Count == 1 ? string.Empty : "s")} {string.Join(", ", exam.Applied)}; 1-3 fails it.";
        _agentPanel!.ShowRating($"Rate exam {number} by its result, not its words.{why}{scores} Words below go to its session first: a revision.");
    }

    /// <summary>The user rated the exam: the verdict goes into the thread, and the lessons it proved are written into skills.</summary>
    private void RateExam(int rating)
    {
        if (_open is not { Busy: false, Waiting: null, Record.Training: { } course } work || Training.OpenExam(course) is not { } exam) return;
        var record = work.Record;
        var proven = Training.Rate(course, exam, rating);
        // The user's rating tells which critic judges as they do: the choice of critics learns from it.
        var agreed = ArtCritic.Calibrate(exam, _projects.ReadWorkerEvidence(work.Project));
        record.Add(AgentEntryKind.You, $"Rated exam {course.Exams.IndexOf(exam) + 1}: {exam.Rating} of 5");
        record.Add(AgentEntryKind.HandOff, Training.Verdict(course, exam, proven) +
                                           (agreed is { } same ? same ? " · the art critic agreed" : " · the art critic did not agree" : string.Empty));
        SaveTask(work);
        ShowTask(work);
        if (proven.Count > 0) _ = StartPromoteAsync(work, course, exam, proven);
        else OfferExam(work, course);
    }

    /// <summary>
    /// The exam's session writes the lessons it proved into skills, low effort: each into the project's general copy of
    /// its pack skill or into the game's add-on, as its lesson says; "Share with all games" follows as after any task.
    /// </summary>
    private async Task StartPromoteAsync(TaskWork work, TrainingCourse course, TrainingExam exam, IReadOnlyList<TrainingLesson> proven)
    {
        var request = File.Exists(Training.PromoteFile) ? await File.ReadAllTextAsync(Training.PromoteFile) : string.Empty;
        if (work.Busy || work.Waiting is not null || !_works.Contains(work)) return;
        var record = work.Record;
        if (exam.SessionId is not { } session || record.Runs.LastOrDefault(run => run.SessionId == session) is not { } last)
        {
            course.Stage = TrainingStage.BetweenExams;
            record.Add(AgentEntryKind.HandOff, "The exam's session is gone: write the proven lessons into the skills by hand, or set another exam");
            SaveTask(work);
            ShowTask(work);
            OfferExam(work, course);
            return;
        }

        var project = work.Project;
        var efforts = _agents.Provider(last.Provider).EffortsOf(last.Model);
        var settings = _settings.Current.Agent;
        // The exam's session holds a whole task: grown big or gone cold, it is not re-read to write a few lessons.
        var fresh = CompactRule.ForStep(last, settings.AutoCompact, settings.ContextBudget, DateTimeOffset.Now);
        var promote = Training.PromotePrompt(request, proven);
        if (fresh is not null) record.Add(AgentEntryKind.HandOff, $"The proven lessons go into skills in a fresh session from the task's state · {fresh}");
        await StartAgentAsync(work, new AgentTask
        {
            Provider = last.Provider,
            Workspace = record.Workspace,
            Prompt = fresh is null ? promote
                : $"{TaskState.FreshBrief(record, null, record.Workspace, "Write the proven lessons below into the skills.", AgentName)}\n\n{promote}",
            ResumeSessionId = fresh is null ? session : null,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace),
            AutoApprove = course.AutoApprove,
            Model = last.Model,
            Effort = efforts.Any(effort => effort.Value == "low") ? "low" : last.Effort,
            RoleId = record.RoleId,
            Skills = record.Skills
        }, work.LastRun is { } previous && AgentCoordinator.SessionOf(previous) == session ? previous : null);
    }

    /// <summary>
    /// Where a training stands, on the task's cards: the lessons as its plan, the exam offered or waiting for its rating,
    /// and what the next words do.
    /// </summary>
    private void ShowTrainingOf(TaskWork work, bool live)
    {
        var panel = _agentPanel!;
        var record = work.Record;
        if (record.Training is not { } course)
        {
            panel.ShowRating(null);
            panel.ShowReplyHint(null);
            return;
        }

        var exam = !live && record.Status != AgentTaskStatus.Finished ? Training.OpenExam(course) : null;
        if (exam is not null && record.Runs.Count > 0 && work.Question is null) ShowRating(record, course, exam);
        else panel.ShowRating(null);
        panel.ShowReplyHint(course.Stage switch
        {
            TrainingStage.AwaitsExam => "The exam: a real task with references (attach images); a new session does it with the candidate lessons",
            TrainingStage.Exam when exam is not null => "A revision: the exam's session goes on with your words before you rate it",
            TrainingStage.BetweenExams => "Your words go on with the specialist in its last session; Ready for exam sets the next exam",
            _ => null
        });
    }
}
