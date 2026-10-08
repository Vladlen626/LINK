using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// What a step or a brief is, from its words, and the tier its kind starts at. The word lists are the ones Auto effort
/// reads a brief by: design or an unknown cause is hard, a mechanical change easy.
/// </summary>
public static class StepKinds
{
    // Stems of the brief's words, English and Russian; what sounds like design or an unknown cause asks for more.
    private static readonly string[] Design =
    [
        "architecture", "design a", "new mechanic", "system", "refactor", "rewrite",
        "архитектур", "спроектир", "механик", "систем", "рефактор", "переписа"
    ];

    private static readonly string[] Unclear =
    [
        "investigate", "why", "crash", "race", "deadlock", "performance", "memory leak", "unclear",
        "разбер", "почему", "вылета", "падает", "утечк", "производител", "непонятн"
    ];

    internal static readonly (string Reason, string[] Words)[] Hard = [("a design task", Design), ("an unclear failure", Unclear)];

    internal static readonly string[] Mechanical =
    [
        "rename", "typo", "import ", "imports", "imported", "reimport", "move file", "format", "translate", "comment", "docs", "description", "bump version",
        "переимен", "опечат", "импорт", "перенеси", "форматир", "перевед", "коммент", "описани", "документац"
    ];

    // The mechanical words that are about text rather than code; a README and a changelog are text too.
    private static readonly string[] Documents = ["comment", "docs", "description", "коммент", "описани", "документац", "readme", "changelog"];

    private static readonly string[] Tests = ["test", "тест"];
    private static readonly string[] Reviews = ["review", "check", "ревью", "проверк", "проверь"];

    /// <summary>
    /// The kind of a step from its words, the first that applies: design, an unclear failure, text (docs, comments), a
    /// mechanical change, tests, a review or check; else code. An asset (an editor or DCC task) is named by the plan,
    /// not guessed from words.
    /// </summary>
    public static StepKind Of(string text)
    {
        if (Design.Any(word => Stems.Found(text, word))) return StepKind.Design;
        if (Unclear.Any(word => Stems.Found(text, word))) return StepKind.Investigate;
        if (Documents.Any(word => Stems.Found(text, word))) return StepKind.Docs;
        if (Mechanical.Any(word => Stems.Found(text, word))) return StepKind.Mechanical;
        if (Tests.Any(word => Stems.Found(text, word))) return StepKind.Test;
        if (Reviews.Any(word => Stems.Found(text, word))) return StepKind.Review;
        return StepKind.Code;
    }

    /// <summary>The tier a kind of step starts at: design, unknown causes and editor work are Strong, mechanical edits and text Light.</summary>
    public static WorkerTier TierOf(StepKind kind) => kind switch
    {
        StepKind.Design or StepKind.Investigate or StepKind.Asset => WorkerTier.Strong,
        StepKind.Mechanical or StepKind.Docs => WorkerTier.Light,
        _ => WorkerTier.Standard
    };
}
