using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// A specialist's training in the Studio: it studies the user's sources read only and draws candidate lessons; on
/// "Ready for exam" the user's next words are an exam, done by a new session of the specialist with the lessons and
/// not the sources; the user rates it, and the lessons a passed exam applied are written into skills in its session.
/// The decisions are <see cref="Training"/>'s; this only starts runs and shows where the training stands.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// A new training: the card's agent studies as the card's specialist, read only, with the web to read; the course
    /// keeps the topic, the sources and the card's Auto for the exams.
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
            Access = AgentAccess.Analyze,
            PlanOnly = false,
            AllowedTools = [.. AgentCoordinator.Tools(project, plan.Workspace, AgentAccess.Analyze).Concat(Training.WebTools).Distinct(StringComparer.Ordinal)],
            CheckCommand = null,
            AutoApprove = false
        }, course);
    }

    /// <summary>
    /// After a run of a training: a study's lessons become the candidates and the exam is offered; an exam's answer tells
    /// what it applied and made, the art critic judges its frames where it made art, and the user is asked to rate it; a
    /// promotion done leaves the training waiting for the next exam. A run that asked something or was cut short waits
    /// for the user's words in its session.
    /// </summary>
    private void AfterTrainingRun(AgentTaskRecord record, AgentRun run, bool needsYou)
    {
        if (record.Training is not { } course) return;
        switch (course.Stage)
        {
            case TrainingStage.Learning:
                var lessons = Training.Lessons(run.Result.Summary);
                if (lessons.Count > 0)
                {
                    course.Lessons = lessons;
                    record.Add(AgentEntryKind.HandOff, $"Candidate lessons: {lessons.Count}, in Lessons above · no worker is told them before an exam proves them");
                    SaveTask(record);
                    ShowTask();
                }

                if (!needsYou && course.Lessons.Count > 0) OfferExam(record, course);
                break;
            case TrainingStage.Exam when Training.OpenExam(course) is { } exam:
                if (ArtCritic.Judges(run.Task))
                {
                    AfterExamCritique(record, course, exam, run, needsYou);
                    break;
                }

                exam.SessionId = AgentCoordinator.SessionOf(run) ?? exam.SessionId;
                Training.ReadExam(run.Result.Summary, exam);
                exam.Files = [.. exam.Files.Union(run.Changes.Select(change => Relative(change.Path)), StringComparer.OrdinalIgnoreCase)];
                // A score of the work before this run says nothing of it now.
                (exam.CriticScore, exam.CriticNotes, exam.CriticProvider, exam.CriticModel) = (null, null, null, null);
                SaveTask(record);
                ShowTask();
                if (!needsYou) CritiqueExam(record, course, exam, run);
                break;
            case TrainingStage.Promoting when !needsYou && WorkerEvidence.Verdict(record.LastRun!) == true:
                course.Stage = TrainingStage.AwaitsExam;
                var left = course.Lessons.Count(lesson => lesson.Status == LessonStatus.Candidate);
                record.Add(AgentEntryKind.HandOff, "The proven lessons are written into the skills · " + (left > 0
                    ? $"{Plural(left, "candidate lesson")} wait for an exam: your next words set it"
                    : "every lesson is proven: Finish the training"));
                SaveTask(record);
                ShowTask();
                break;
        }
    }

    /// <summary>
    /// The art critic judges an exam's frames before the user rates them, where the exam made art the critic judges
    /// (<see cref="ArtCritic.Judgeable"/>) and saved it; the thread says why when no critic can.
    /// </summary>
    private void CritiqueExam(AgentTaskRecord record, TrainingCourse course, TrainingExam exam, AgentRun run)
    {
        if (!_settings.Current.Agent.Team.ArtReview || !ArtCritic.Judgeable(run.Task) || exam.Files.Count == 0) return;
        var number = course.Exams.IndexOf(exam) + 1;
        var frames = ArtCritic.Frames(exam.Sheet, record.Workspace, File.Exists);
        var (critic, why) = frames.Count == 0
            ? (null, "it listed no frames in its sheet")
            : CriticFor(new Worker(run.Task.Provider, AgentLevel.Deep, run.Task.Model), _projects.ReadWorkerEvidence(_taskProject ?? _projects.ActiveProject));
        record.Add(AgentEntryKind.HandOff, critic is null
            ? $"Exam {number}: no art critic judged it · {why}"
            : $"Exam {number}: {WorkerLabel(critic)} judges its frames before you rate it, blind to its answer · {why}");
        SaveTask(record);
        ShowTask();
        if (critic is not null) Dispatcher.BeginInvoke(() => _ = StartCriticAsync(record, critic, run, exam.Brief, null, exam.Files, exam.Images, frames));
    }

    /// <summary>
    /// The art critic's run on an exam ended: its score and defects go beside the rating, and the user's words after it go
    /// on in the exam's session. A question of the critic's waits for the user's answer in its session.
    /// </summary>
    private void AfterExamCritique(AgentTaskRecord record, TrainingCourse course, TrainingExam exam, AgentRun run, bool needsYou)
    {
        if (needsYou) return;
        if (_critiqued is { } judged) _lastRun = judged;
        _critiqued = null;
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

        SaveTask(record);
        ShowTask();
    }

    /// <summary>The study has drawn its lessons: words go on talking them over, "Ready for exam" makes the next words the exam.</summary>
    private void OfferExam(AgentTaskRecord record, TrainingCourse course)
    {
        _agentPanel!.ShowDecision(
            $"{Plural(course.Lessons.Count, "candidate lesson")} in Lessons above. Talk them over with the specialist below, or set the exam: " +
            "a real task of this project with references, done by a new session that has these lessons and not the sources.",
            "Ready for exam",
            canRemember: false);
        _decision = () =>
        {
            course.Stage = TrainingStage.AwaitsExam;
            record.Add(AgentEntryKind.You, "Ready for exam");
            SaveTask(record);
            ShowTask();
        };
    }

    /// <summary>
    /// The user's words set the exam: a new session of the training's agent as its specialist, with edits, told the
    /// candidate lessons, the rubric and the task; the user's images go along as the references.
    /// </summary>
    private async Task StartExamAsync(AgentTaskRecord record, TrainingCourse course, string words, IReadOnlyList<string> images)
    {
        var project = _taskProject ?? _projects.ActiveProject;
        var learner = record.Runs.FirstOrDefault();
        var provider = learner?.Provider ?? StartProvider;
        var request = File.Exists(Training.ExamFile) ? await File.ReadAllTextAsync(Training.ExamFile) : string.Empty;
        if (AgentRunning || _task != record) return;
        var exam = new TrainingExam { Brief = words, Provider = provider, Model = learner?.Model, Images = [.. images] };
        course.Exams.Add(exam);
        course.Stage = TrainingStage.Exam;
        var candidates = course.Lessons.Count(lesson => lesson.Status == LessonStatus.Candidate);
        record.Add(AgentEntryKind.You, words + ImagesNote(images));
        record.Add(AgentEntryKind.HandOff, $"Exam {course.Exams.Count}: a new session of {AgentName(provider)} with {Plural(candidates, "candidate lesson")}, not the sources");
        SaveTask(record);
        var task = new AgentTask
        {
            Provider = provider,
            Workspace = record.Workspace,
            Prompt = Training.ExamPrompt(request, course, ProjectRoles.Find(record.RoleId, record.Workspace), words) +
                     (project.CheckCommand is { } check ? "\n\n" + AgentCoordinator.CheckNote(check) : string.Empty),
            Images = images,
            Access = AgentAccess.Do,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace, AgentAccess.Do),
            CheckCommand = project.CheckCommand,
            AutoApprove = course.AutoApprove,
            Model = learner?.Model,
            Effort = learner?.Effort,
            RoleId = record.RoleId,
            Skills = record.Skills
        };
        _agentOrigin = task;
        await StartAgentAsync(task, null, phase: EffortPhase.New);
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
        if (AgentRunning || _task is not { Training: { } course } record || Training.OpenExam(course) is not { } exam) return;
        var proven = Training.Rate(course, exam, rating);
        // The user's rating tells which critic judges as they do: the choice of critics learns from it.
        var agreed = ArtCritic.Calibrate(exam, _projects.ReadWorkerEvidence(_taskProject ?? _projects.ActiveProject));
        record.Add(AgentEntryKind.You, $"Rated exam {course.Exams.IndexOf(exam) + 1}: {exam.Rating} of 5");
        record.Add(AgentEntryKind.HandOff, Training.Verdict(course, exam, proven) +
                                           (agreed is { } same ? same ? " · the art critic agreed" : " · the art critic did not agree" : string.Empty));
        SaveTask(record);
        ShowTask();
        if (proven.Count > 0) _ = StartPromoteAsync(record, course, exam, proven);
    }

    /// <summary>
    /// The exam's session writes the lessons it proved into skills, low effort: each into the project's general copy of
    /// its pack skill or into the game's add-on, as its lesson says; "Share with all games" follows as after any task.
    /// </summary>
    private async Task StartPromoteAsync(AgentTaskRecord record, TrainingCourse course, TrainingExam exam, IReadOnlyList<TrainingLesson> proven)
    {
        var request = File.Exists(Training.PromoteFile) ? await File.ReadAllTextAsync(Training.PromoteFile) : string.Empty;
        if (AgentRunning || _task != record) return;
        if (exam.SessionId is not { } session || record.Runs.LastOrDefault(run => run.SessionId == session) is not { } last)
        {
            course.Stage = TrainingStage.AwaitsExam;
            record.Add(AgentEntryKind.HandOff, "The exam's session is gone: write the proven lessons into the skills by hand, or set another exam");
            SaveTask(record);
            ShowTask();
            return;
        }

        var project = _taskProject ?? _projects.ActiveProject;
        var efforts = _agents.Provider(last.Provider).EffortsOf(last.Model);
        await StartAgentAsync(new AgentTask
        {
            Provider = last.Provider,
            Workspace = record.Workspace,
            Prompt = Training.PromotePrompt(request, proven),
            ResumeSessionId = session,
            Access = AgentAccess.Do,
            AllowedTools = AgentCoordinator.Tools(project, record.Workspace, AgentAccess.Do),
            AutoApprove = course.AutoApprove,
            Model = last.Model,
            Effort = efforts.Any(effort => effort.Value == "low") ? "low" : last.Effort,
            RoleId = record.RoleId,
            Skills = record.Skills
        }, _lastRun is { } previous && AgentCoordinator.SessionOf(previous) == session ? previous : null);
    }

    /// <summary>
    /// Where a training stands, on the task's cards: the lessons as its plan, the exam offered or waiting for its rating,
    /// and what the next words do.
    /// </summary>
    private void ShowTrainingOf(AgentTaskRecord record, bool live)
    {
        var panel = _agentPanel!;
        if (record.Training is not { } course)
        {
            panel.ShowRating(null);
            panel.ShowReplyHint(null);
            return;
        }

        var exam = !live && record.Status != AgentTaskStatus.Finished ? Training.OpenExam(course) : null;
        if (exam is not null && record.Runs.Count > 0 && _question is null) ShowRating(record, course, exam);
        else panel.ShowRating(null);
        panel.ShowReplyHint(course.Stage switch
        {
            TrainingStage.AwaitsExam => "The exam: a real task with references (attach images); a new session does it with the candidate lessons",
            TrainingStage.Exam when exam is not null => "A revision: the exam's session goes on with your words before you rate it",
            _ => null
        });
    }
}
