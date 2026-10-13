using System.Globalization;

namespace Link.Desktop.Services;

/// <summary>How the size of an agent's context is told: "182k", and "Context 182k / 400k (46%)" against the limit.</summary>
public static class AgentContext
{
    public static string Tokens(long number) => number switch
    {
        < 1_000 => number.ToString(CultureInfo.InvariantCulture),
        < 100_000 => (number / 1_000.0).ToString("0.#k", CultureInfo.InvariantCulture),
        < 1_000_000 => (number / 1_000.0).ToString("0k", CultureInfo.InvariantCulture),
        _ => (number / 1_000_000.0).ToString("0.#M", CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// The limit a session is measured against: a set budget, capped by the model's window; on Auto (budget 0) the CLI
    /// compacts by its own window for the model, which Link is not told, so the model's window. Null: neither is known.
    /// </summary>
    public static long? Limit(int budget, long? window) => budget > 0 ? Math.Min(budget, window ?? budget) : window;

    /// <summary>What the session holds against <see cref="Limit"/>.</summary>
    public static string Line(long used, long? window, int budget) => "Context " + Usage(used, window, budget);

    /// <summary>"182k / 400k (46%)": the line without its word, for a place that has the word already.</summary>
    public static string Usage(long used, long? window, int budget)
    {
        if (Limit(budget, window) is not { } limit || limit <= 0) return Tokens(used);
        var percent = (int)Math.Round(used * 100.0 / limit, MidpointRounding.AwayFromZero);
        return $"{Tokens(used)} / {Tokens(limit)} ({percent}%)";
    }

    /// <summary>"Auto" or "400k": the budget as Settings and the card say it.</summary>
    public static string Budget(int budget) => budget > 0 ? Tokens(budget) : "Auto";
}
