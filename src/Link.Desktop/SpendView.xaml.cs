using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>A part in the legend under the task's bar: its colour, name and share.</summary>
public sealed record SpendLegendRow(SpendPart Part, string Name, string Share, string Hint, Brush Brush);

/// <summary>A tip as the view lists it, with the button of its fix where it has one.</summary>
public sealed record SpendTipRow(SpendTip Tip, Brush Brush, string FixLabel, string FixHint)
{
    public string Text => Tip.Text;
    public string Why => Tip.Why;
    public Visibility FixVisibility => Tip.Fix == SpendFix.None ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A run in the task's list: its bar as long as its share of the largest run.</summary>
public sealed record SpendRunRow(string Name, string Detail, string Amount, SpendParts Parts, double Scale);

/// <summary>A usage window: its label, how much is used, the bar and what the pace says.</summary>
public sealed record SpendGaugeRow(string Label, string Used, SpendGauge Gauge, string Note, Brush NoteBrush);

/// <summary>
/// The Spend view, the Studio's other column: where the open task's tokens went and what to do about it, the agents'
/// windows, and the project's days. It only shows; the window builds the report (<see cref="SpendReport"/>) and does
/// the fixes. Its one timer counts the cache down, only while the view is shown and the cache is warm.
/// </summary>
public partial class SpendView : UserControl
{
    private readonly DispatcherTimer _cacheTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SpendTask? _task;
    private bool _working;
    private IReadOnlyList<SpendDay> _days = [];
    private bool _ready;

    public SpendView()
    {
        InitializeComponent();
        _cacheTimer.Tick += (_, _) => ShowCache();
        IsVisibleChanged += (_, _) => ShowCache();
        History.DayHovered += ShowReadout;
    }

    /// <summary>A tip's button: the map is to be written or updated, or the task compacted.</summary>
    public event Action<SpendFix>? FixRequested;

    /// <summary>The slider or the scope changed: the window gives the days again (<see cref="ShowHistory"/>).</summary>
    public event Action? HistoryChanged;

    public int RangeDays => (int)RangeSlider.Value;

    public bool AllProjects => AllProjectsBox.IsChecked == true;

    /// <summary>The range and scope the view starts with, as the Studio's settings keep them; no change is told.</summary>
    public void Start(int days, bool allProjects)
    {
        RangeSlider.Value = Math.Clamp(days, (int)RangeSlider.Minimum, (int)RangeSlider.Maximum);
        AllProjectsBox.IsChecked = allProjects;
        RangeText.Text = RangeWords(RangeDays);
        _ready = true;
    }

    /// <summary>The task's spend (null: none is open) while its agent works or not, and the agents' windows.</summary>
    public void Show(SpendTask? task, bool working, IReadOnlyList<SpendGauge> gauges)
    {
        _task = task;
        _working = working;
        GoalText.Text = task?.Goal ?? "No task open";
        GoalText.ToolTip = task?.Goal;
        EmptyText.Visibility = task is null ? Visibility.Visible : Visibility.Collapsed;
        TaskPanel.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        ShowTask(task);
        ShowTips(task);
        ShowCurve(task);
        ShowRuns(task);
        ShowGauges(gauges);
        ShowCache();
    }

    /// <summary>The days of the range, oldest first, every one of them.</summary>
    public void ShowHistory(IReadOnlyList<SpendDay> days)
    {
        _days = days;
        History.Days = days;
        HistoryEmptyText.Visibility = days.All(day => day.Parts.Total == 0) ? Visibility.Visible : Visibility.Collapsed;
        ShowReadout(null);
    }

    private void ShowTask(SpendTask? task)
    {
        if (task is null) return;
        var total = task.Parts.Total + (long)task.Unsplit;
        TotalText.Text = "~" + AgentContext.Tokens(total);
        CostText.Text = task.CostUsd is { } cost ? Money(cost) : "—";
        CostNoteText.Text = task.CostUsd is null ? "no price told" : "at API prices";
        var work = new List<string> { Duration(task.Time) };
        if (task.Turns > 0) work.Add(Plural(task.Turns, "turn"));
        if (task.ToolCalls > 0) work.Add(Plural(task.ToolCalls, "tool call"));
        if (task.ToolErrors > 0) work.Add(Plural(task.ToolErrors, "error"));
        if (task.WindowPoints is { } points) work.Add($"{points.ToString("0.#", CultureInfo.InvariantCulture)}% of the 5 h window");
        WorkText.Text = string.Join(" · ", work);
        TaskBar.Parts = task.Parts;
        TaskBar.Highlight = null;
        LegendList.ItemsSource = SpendParts.All.Where(part => task.Parts[part] > 0)
            .Select(part => new SpendLegendRow(part, SpendReport.Name(part), $"{Math.Round(100.0 * task.Parts[part] / Math.Max(1, task.Parts.Total))}%",
                SpendReport.Hint(part), SpendInk.Part(this, part)))
            .ToArray();
        UnsplitText.Text = task.Unsplit > 0
            ? task.Parts.Total > 0
                ? $"~{AgentContext.Tokens((long)task.Unsplit)} of runs from before Link split the tokens are in the total, not in the bar."
                : "These runs are from before Link split the tokens: the next run's show where they go."
            : string.Empty;
        UnsplitText.Visibility = task.Unsplit > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTips(SpendTask? task)
    {
        var tips = task?.Tips ?? [];
        TipsCard.Visibility = tips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TipsList.ItemsSource = tips.Select(tip => new SpendTipRow(tip,
            tip.Part is { } part ? SpendInk.Part(this, part) : SpendInk.Brush(this, "AccentBrush"),
            tip.Fix switch { SpendFix.Map => task!.HasMap ? "Update map" : "Write map", SpendFix.Compact => "Compact", _ => string.Empty },
            tip.Fix switch
            {
                SpendFix.Map => "Link's own run reads the project and writes docs/map.md: what is where, for every task after",
                SpendFix.Compact => "A fresh session of the same agent from the task's state; you see and can change the state first",
                _ => string.Empty
            })).ToArray();
    }

    private void ShowCurve(SpendTask? task)
    {
        var shown = task is { Curve.Count: > 1 };
        CurveCard.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        Curve.Points = task!.Curve;
        Curve.Budget = task.Budget;
        CurveLabelText.Text = task.CurveLabel;
    }

    private void ShowRuns(SpendTask? task)
    {
        var runs = task?.Runs ?? [];
        RunsCard.Visibility = runs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var most = Math.Max(1, runs.Count == 0 ? 1 : runs.Max(run => run.Parts.Total));
        RunsList.ItemsSource = runs.Select(run => new SpendRunRow(run.Name, run.Detail,
            run.CostUsd is { } cost ? $"~{AgentContext.Tokens(run.Parts.Total)} · {Money(cost)}" : "~" + AgentContext.Tokens(run.Parts.Total),
            run.Parts, Math.Max(0.02, (double)run.Parts.Total / most))).ToArray();
    }

    private void ShowGauges(IReadOnlyList<SpendGauge> gauges)
    {
        GaugesCard.Visibility = gauges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GaugesList.ItemsSource = gauges.Select(gauge => new SpendGaugeRow(gauge.Label,
            $"{Math.Round(gauge.Used)}%{(gauge.Mine > 0 ? $" · this task {gauge.Mine.ToString("0.#", CultureInfo.InvariantCulture)}" : string.Empty)}",
            gauge, gauge.Note, SpendInk.Brush(this, gauge.Warn ? "ErrorBrush" : "MutedBrush"))).ToArray();
    }

    /// <summary>
    /// How long the session's cache stays warm: a turn in it reads the context at a tenth of the price, a turn after it
    /// reads all again at full price. While the agent works the cache is in use.
    /// </summary>
    private void ShowCache()
    {
        if (_task is not { CacheWarmUntil: { } until } task)
        {
            CacheRow.Visibility = Visibility.Collapsed;
            _cacheTimer.Stop();
            return;
        }

        CacheRow.Visibility = Visibility.Visible;
        var context = task.ContextNow is { } now ? "~" + AgentContext.Tokens(now) : "the context";
        var left = until - DateTimeOffset.Now;
        if (_working)
        {
            CacheDot.Fill = SpendInk.Brush(this, "BusyOrbBrush");
            CacheText.Text = "Cache in use";
            CacheNoteText.Text = "The agent works: each request reads what came before from the cache.";
        }
        else if (left > TimeSpan.Zero)
        {
            CacheDot.Fill = SpendInk.Brush(this, "SuccessBrush");
            CacheText.Text = $"Cache warm · {(int)left.TotalMinutes}:{left.Seconds:00} left";
            CacheNoteText.Text = $"A reply now reads {context} from the cache, at a tenth of the price.";
        }
        else
        {
            CacheDot.Fill = SpendInk.Brush(this, "IdleOrbBrush");
            CacheText.Text = "Cache cold";
            CacheNoteText.Text = $"The next turn reads {context} again at full price. Compact makes it small.";
        }

        if (IsVisible && !_working && left > TimeSpan.Zero) _cacheTimer.Start();
        else _cacheTimer.Stop();
    }

    /// <summary>Under the history: the day under the pointer, or the whole range when there is none.</summary>
    private void ShowReadout(SpendDay? day)
    {
        if (day is { } one)
        {
            ReadoutText.Text = $"{one.Day.ToString("ddd, MMM d", CultureInfo.InvariantCulture)} · {(one.Runs == 0 ? "no runs" : Plural(one.Runs, "run"))}"
                               + (one.Parts.Total > 0 ? $" · ~{AgentContext.Tokens(one.Parts.Total)}" : string.Empty);
            ReadoutCostText.Text = one.CostUsd is { } cost ? Money(cost) : string.Empty;
            ReadoutBar.Parts = one.Parts;
            return;
        }

        var parts = _days.Aggregate(new SpendParts(), (sum, each) => sum + each.Parts);
        var runs = _days.Sum(each => each.Runs);
        ReadoutText.Text = $"{RangeWords(_days.Count)} · {Plural(runs, "run")} · ~{AgentContext.Tokens(parts.Total)}";
        ReadoutCostText.Text = _days.Any(each => each.CostUsd is not null) ? Money(_days.Sum(each => each.CostUsd ?? 0)) : string.Empty;
        ReadoutBar.Parts = parts;
    }

    private void Legend_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpendLegendRow row }) TaskBar.Highlight = row.Part;
    }

    private void Legend_OnMouseLeave(object sender, MouseEventArgs e) => TaskBar.Highlight = null;

    private void Fix_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SpendTipRow row }) FixRequested?.Invoke(row.Tip.Fix);
    }

    private void Range_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RangeText is null) return;
        RangeText.Text = RangeWords(RangeDays);
        if (_ready) HistoryChanged?.Invoke();
    }

    private void Scope_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) HistoryChanged?.Invoke();
    }

    private static string RangeWords(int days) => $"Last {days} days";

    private static string Money(decimal usd) => usd < 0.01m ? "<$0.01" : "$" + usd.ToString(usd < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan time) => time.TotalMinutes < 1 ? $"{Math.Max(1, (int)time.TotalSeconds)} s"
        : time.TotalHours < 1 ? $"{(int)time.TotalMinutes} min"
        : $"{(int)time.TotalHours} h {time.Minutes} min";

    private static string Plural(int count, string what) => $"{count} {what}{(count == 1 ? string.Empty : "s")}";
}
