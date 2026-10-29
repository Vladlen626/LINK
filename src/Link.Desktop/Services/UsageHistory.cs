using System.Text.Json;
using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// One day of a project's work by one agent, added up from its runs: what the Spend view's history shows. A task's
/// record keeps only the last tasks; this keeps <see cref="UsageHistory.KeptDays"/> days of all of them.
/// </summary>
public sealed record UsageDay
{
    public DateOnly Day { get; init; }
    public Guid Project { get; init; }
    public AgentProviderKind Provider { get; init; }
    public int Runs { get; init; }
    public long Uncached { get; init; }
    public long Cached { get; init; }
    public long Output { get; init; }
    public decimal? CostUsd { get; init; }
    public TimeSpan Time { get; init; }
    public SpendParts Spend { get; init; } = new();

    /// <summary>Percentage points of each usage window the day's runs used, where the CLI reported them.</summary>
    public Dictionary<string, double> Windows { get; init; } = [];

    /// <summary>The day's ≈ tokens: its parts' sum, or for days before the split was kept, its tokens' worth.</summary>
    public double Equivalent => Spend.Total > 0 ? Spend.Total : new TokenCount(Uncached, Cached, Output).Equivalent;

    /// <summary>A day of no older Link's making: no numbers below zero, and the parts always there.</summary>
    public UsageDay Normalize() => this with
    {
        Runs = Math.Max(0, Runs),
        Uncached = Math.Max(0, Uncached),
        Cached = Math.Max(0, Cached),
        Output = Math.Max(0, Output),
        Spend = Spend ?? new SpendParts(),
        Windows = Windows ?? []
    };
}

/// <summary>
/// The days of work, per project and agent, in <c>usage-history.json</c>: each run adds to its day as it ends. Read
/// when first needed, not at Link's start; days older than <see cref="KeptDays"/> are dropped as the next run is saved.
/// </summary>
public sealed class UsageHistory(string file)
{
    public const int KeptDays = 90;

    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private List<UsageDay>? _days;

    private List<UsageDay> Days => _days ??= [.. (AtomicJsonFile.Read<List<UsageDay>>(file, _json) ?? []).Select(day => day.Normalize())];

    /// <summary>A run joins its day: the day it started on, in this computer's time.</summary>
    public void Add(Guid project, AgentRunRecord run)
    {
        var day = DateOnly.FromDateTime(run.StartedAt.LocalDateTime);
        var days = Days;
        var at = days.FindIndex(each => each.Day == day && each.Project == project && each.Provider == run.Provider);
        var old = at >= 0 ? days[at] : new UsageDay { Day = day, Project = project, Provider = run.Provider };
        var tokens = TokenCount.Of(run);
        var windows = new Dictionary<string, double>(old.Windows);
        foreach (var spent in run.Spent) windows[spent.Window] = Math.Round(windows.GetValueOrDefault(spent.Window) + spent.Percent, 1);
        var added = old with
        {
            Runs = old.Runs + 1,
            Uncached = old.Uncached + tokens.Uncached,
            Cached = old.Cached + tokens.Cached,
            Output = old.Output + tokens.Output,
            CostUsd = old.CostUsd is null && run.CostUsd is null ? null : (old.CostUsd ?? 0) + (run.CostUsd ?? 0),
            Time = old.Time + run.Duration,
            Spend = old.Spend + (run.Spend ?? SpendMeter.Split(null, tokens)),
            Windows = windows
        };
        if (at >= 0) days[at] = added;
        else days.Add(added);
        var first = DateOnly.FromDateTime(DateTime.Today).AddDays(-KeptDays + 1);
        days.RemoveAll(each => each.Day < first);
        try
        {
            AtomicJsonFile.Write(file, days, _json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The day is still counted while Link runs; the next run's save writes it.
        }
    }

    /// <summary>The days from <paramref name="first"/> on, of one project or of all.</summary>
    public IReadOnlyList<UsageDay> Since(DateOnly first, Guid? project = null) =>
        [.. Days.Where(day => day.Day >= first && (project is null || day.Project == project)).OrderBy(day => day.Day)];
}
