using System.Text;
using System.Text.RegularExpressions;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// The art critic, without the UI: whether a run's work is art it judges, from which frames, who judges it and what it
/// is told, and whether its score agreed with the user's rating of an exam, which the choice of critics learns from.
/// The critic judges blind to the artist's account of the work, so that the artist's words do not stand in for the
/// look. The request is markdown beside Link.exe, edited without a rebuild.
/// </summary>
public static partial class ArtCritic
{
    /// <summary>The critic's role, one of the built-in specialists.</summary>
    public const string RoleId = "art-critic";

    /// <summary>The skill of the project's art profile: a specialist that works by it makes art the critic judges.</summary>
    public const string StyleSkill = "art-style";

    /// <summary>So many frames go to the critic as images; it is told the paths of the others, to open what it needs.</summary>
    public const int MostFrames = 6;

    // A sheet's mark lies right after the PNG's header (see IsSheet): so much of the file is read for it.
    private const int SheetHead = 1024;

    // What a critic's own session is told after a redo, before the new frames.
    private const string Again =
        "The artist has worked on the defects you named and rendered its frames again, below. Judge them as before, from " +
        "the frames and saved files alone: is each defect fixed, and what is the worst now? End your answer with the " +
        "link-review block, nothing after it.";

    /// <summary>So many saved files the critic is told of; more are a list, not a lead.</summary>
    private const int MostSaved = 20;

    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg"];

    public static string RequestFile => Path.Combine(AppContext.BaseDirectory, "harness", "art-review.md");

    /// <summary>Whether a run is the critic's judgement: read only, as the critic.</summary>
    public static bool Judges(AgentTask task) => task.RoleId == RoleId && task.Access == AgentAccess.Analyze;

    /// <summary>Whether a run the task's record kept was the critic's judgement (see <see cref="Judges"/>).</summary>
    public static bool Judged(AgentRunRecord run) => run.RoleId == RoleId && run.Access == AgentAccess.Analyze;

    /// <summary>Whether a run made art the critic judges: its specialist works by <see cref="StyleSkill"/> and is not the critic.</summary>
    public static bool Judgeable(AgentTask task) =>
        task.RoleId != RoleId && task.Skills.Contains(StyleSkill, StringComparer.OrdinalIgnoreCase);

    /// <summary>The paths of the images an answer names, in backticks (where a path may hold spaces) or bare, in the order named.</summary>
    public static IReadOnlyList<string> Named(string answer) =>
        [.. ImagePath().Matches(answer).Select(match => (match.Groups[1].Success ? match.Groups[1] : match.Groups[2]).Value.Trim())];

    /// <summary>
    /// The frames among <paramref name="named"/> that are images and exist (<paramref name="exists"/>): full paths, a
    /// relative one from the workspace, each once, in their order. A path that cannot be a file's is none.
    /// </summary>
    public static IReadOnlyList<string> Frames(IEnumerable<string> named, string workspace, Func<string, bool> exists)
    {
        var frames = new List<string>();
        foreach (var path in named)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (ImageTypes.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) &&
                !frames.Contains(full, StringComparer.OrdinalIgnoreCase) && exists(full))
            {
                frames.Add(full);
            }
        }

        return frames;
    }

    /// <summary>
    /// The frames that go to the critic as images: the sheets among them (<paramref name="isSheet"/>) where there are
    /// any, since a sheet holds the frames it was glued from, which the critic is told by path; else the first
    /// <see cref="MostFrames"/>.
    /// </summary>
    public static IReadOnlyList<string> Attached(IReadOnlyList<string> frames, Func<string, bool> isSheet)
    {
        var sheets = frames.Where(isSheet).ToArray();
        return [.. (sheets.Length > 0 ? sheets : frames).Take(MostFrames)];
    }

    /// <summary>Whether an image is a sheet that <c>sheet.py</c> glued from frames: it marks the PNG so, in a text chunk after its header.</summary>
    public static bool IsSheet(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[SheetHead];
            var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return head.AsSpan(0, read).IndexOf("tEXtLink\0sheet"u8) >= 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Who judges an artist's work, and why in words. Another subscription agent than the artist's comes first, at Deep,
    /// so that one model's taste does not pass its own blind spots; else the artist's own agent, in a new session that
    /// sees no account of the work either. An agent that cannot judge now (not set up, at its limit, no eyes) or that the
    /// project's evidence has disagreeing with the user (<see cref="StepKind.Critique"/>) is passed over; one the evidence
    /// has proven goes first. No critic when none can judge, and the reason says why.
    /// </summary>
    public static (Worker? Critic, string Reason) Choose(Worker artist, AgentSettings settings, Func<AgentProviderKind, WorkerState> state,
        WorkerEvidence evidence, Func<AgentProviderKind, bool> seesImages, Func<AgentProviderKind, string> agentName)
    {
        var able = new List<Worker>();
        var passed = new List<string>();
        foreach (var provider in new[] { AgentProviderKind.ClaudeCode, AgentProviderKind.Codex }.OrderBy(provider => provider == artist.Provider))
        {
            var critic = new Worker(provider, AgentLevel.Deep, settings.Level(provider, AgentLevel.Deep).Model);
            var (agreed, disagreed) = evidence.Counts(StepKind.Critique, critic);
            var name = agentName(provider);
            var why = state(provider) switch
            {
                WorkerState.NotSet => $"{name} is not set up",
                WorkerState.LimitReached => $"{name} limit reached",
                _ when !seesImages(provider) => $"{name} cannot see images",
                _ when evidence.Blocked(StepKind.Critique, critic) => $"{name} disagreed with you {disagreed} of {agreed + disagreed} here",
                _ => null
            };
            if (why is null) able.Add(critic);
            else passed.Add(why);
        }

        if (able.Count == 0) return (null, "no agent can judge it now: " + string.Join(", ", passed));
        var chosen = able.FirstOrDefault(critic => evidence.Proven(StepKind.Critique, critic)) ?? able[0];
        var (done, failed) = evidence.Counts(StepKind.Critique, chosen);
        var own = chosen.Provider == artist.Provider;
        var reasons = own ? passed : [];
        reasons.Add(evidence.Proven(StepKind.Critique, chosen) ? $"agreed with you {done} of {done + failed} here"
            : own ? "a new session of the artist's agent"
            : "another agent than the artist's");
        return (chosen, string.Join(", ", reasons));
    }

    /// <summary>
    /// What the critic's new session is told: the request (the text of <see cref="RequestFile"/>), the task, what the
    /// artist handed over and saved, the user's references and the frames by path, those <paramref name="attached"/> as
    /// images marked so (see <see cref="Attached"/>). Never the artist's account of its work: the critic judges what was made.
    /// </summary>
    public static string Prompt(string request, string task, string? handedOver, IReadOnlyList<string> saved, IReadOnlyList<string> references,
        IReadOnlyList<string> frames, IReadOnlyList<string> attached)
    {
        var text = new StringBuilder(AgentRules.WithoutNotes(request).Trim()).Append("\n\n## The task\n").Append(task.Trim());
        if (handedOver?.Trim() is { Length: > 0 } handed) text.Append("\n\n## Handed over\n").Append(handed);
        Saved(text, saved);
        if (references.Count > 0) text.Append("\n\n## The user's references\n").AppendJoin('\n', references.Select(image => "- " + image));
        return Framed(text, frames, attached);
    }

    /// <summary>
    /// What the critic's own session is told when the artist has redone the step it sent back: the files saved now and
    /// the new frames, attached as <see cref="Prompt"/> does. The session knows the task, the profile and the defects it
    /// named; it still sees no account of the artist's.
    /// </summary>
    public static string AgainPrompt(IReadOnlyList<string> saved, IReadOnlyList<string> frames, IReadOnlyList<string> attached)
    {
        var text = new StringBuilder(Again);
        Saved(text, saved);
        return Framed(text, frames, attached);
    }

    private static void Saved(StringBuilder text, IReadOnlyList<string> saved)
    {
        if (saved.Count == 0) return;
        text.Append("\n\n## Saved files\n").AppendJoin('\n', saved.Take(MostSaved).Select(file => "- " + file));
        if (saved.Count > MostSaved) text.Append($"\n- … and {saved.Count - MostSaved} more");
    }

    private static string Framed(StringBuilder text, IReadOnlyList<string> frames, IReadOnlyList<string> attached) =>
        text.Append("\n\n## Frames\n")
            .AppendJoin('\n', frames.Select(frame => $"- {frame}{(attached.Contains(frame, StringComparer.OrdinalIgnoreCase) ? " (attached)" : string.Empty)}"))
            .ToString();

    /// <summary>What the artist's own session is told when the critic sends its step back: the defects, the source, the same frames again.</summary>
    public static string RedoPrompt(string notes) =>
        $"The art critic judged your frames and asks for this, worst first:\n{notes.Trim()}\nFix it in the source: your changes are in the " +
        $"workspace. Render the same frames again and name each by its full path. {TeamPlan.StepEnd}";

    /// <summary>The user's words to the artist after the critic left the step to them, with what the critic said in its own session.</summary>
    public static string Heard(string notes, string words) =>
        $"The art critic judged your frames and said this, worst first:\n{notes.Trim()}\n\nThe user has seen it and says:\n{words.Trim()}";

    /// <summary>Whether the critic's score agreed with the user's rating of the same work: both pass (<see cref="Training.PassRating"/> and up) or both fail.</summary>
    public static bool Agrees(int score, int rating) => score >= Training.PassRating == rating >= Training.PassRating;

    /// <summary>
    /// Counts the critic of a rated exam by whether its score agreed with the user's rating, and returns whether it did;
    /// null, and nothing counted, for an exam no critic scored or one that proves nothing (unverified).
    /// </summary>
    public static bool? Calibrate(TrainingExam exam, WorkerEvidence evidence)
    {
        if (exam is not { CriticScore: { } score, CriticProvider: { } provider, Rating: { } rating } || exam.Result == ExamResult.Unverified) return null;
        var agreed = Agrees(score, rating);
        evidence.Record(StepKind.Critique, new Worker(provider, AgentLevel.Deep, exam.CriticModel), agreed);
        return agreed;
    }

    // An image's path in backticks, which may hold spaces, or bare up to a space, a quote or a bracket.
    [GeneratedRegex(@"`([^`\r\n]+?\.(?:png|jpe?g))`|([^\s`'""<>|*?()\[\]]+\.(?:png|jpe?g))(?!\w)", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePath();
}
