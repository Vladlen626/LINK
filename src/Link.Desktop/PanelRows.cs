using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Link.Desktop.Models;

namespace Link.Desktop;

// What the panel's lists and threads show, one record per row; the panels draw them and the card fills them.

/// <summary>The user's words in a thread: a task's brief, a reply or a correction, a question to the chat.</summary>
public sealed record ThreadUser(string Text, string Time, string Carried = "")
{
    public Visibility CarriedVisibility => Carried.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A line between the turns of a task: a hand-over, another model, a limit, the task finished.</summary>
public sealed record ThreadNote(string Text, string Time);

/// <summary>A chat's answer in its thread, cut to a glance; Show puts the whole of it in Link's card.</summary>
public sealed record ThreadAnswer(string Text, string Time, ProjectExchange Exchange);

/// <summary>
/// An agent's run in its task's thread: who worked and on what, its steps folded, its answer, the files it changed and
/// what it cost. The running one fills as the events come.
/// </summary>
public sealed class ThreadTurn(string who, string outcome, Brush outcomeBrush) : INotifyPropertyChanged
{
    private const int StepsKept = 300;
    private string _outcome = outcome;
    private Brush _outcomeBrush = outcomeBrush;
    private string _current = string.Empty;
    private string _said = string.Empty;
    private IReadOnlyList<AgentChangeRow> _files = [];
    private string _stats = string.Empty;
    private bool _stepsOpen;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>"Claude Code · Opus 5.5 · Extra high".</summary>
    public string Who { get; } = who;

    public ObservableCollection<AgentStepRow> Steps { get; } = [];

    /// <summary>"Done in 3:12", "Working · 1:05", "Stopped after 0:40".</summary>
    public string Outcome
    {
        get => _outcome;
        set => Set(ref _outcome, value, nameof(Outcome));
    }

    public Brush OutcomeBrush
    {
        get => _outcomeBrush;
        set => Set(ref _outcomeBrush, value, nameof(OutcomeBrush));
    }

    /// <summary>The step under way while the agent works; empty after the run.</summary>
    public string Current
    {
        get => _current;
        set => Set(ref _current, value, nameof(Current), nameof(CurrentVisibility));
    }

    public Visibility CurrentVisibility => _current.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The agent's answer at the end of the run.</summary>
    public string Said
    {
        get => _said;
        set => Set(ref _said, value, nameof(Said), nameof(SaidVisibility));
    }

    public Visibility SaidVisibility => _said.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<AgentChangeRow> Files
    {
        get => _files;
        set => Set(ref _files, value, nameof(Files), nameof(FilesVisibility));
    }

    public Visibility FilesVisibility => _files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The run's numbers and check in a line or two.</summary>
    public string Stats
    {
        get => _stats;
        set => Set(ref _stats, value, nameof(Stats), nameof(StatsVisibility));
    }

    public Visibility StatsVisibility => _stats.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public bool StepsOpen
    {
        get => _stepsOpen;
        set => Set(ref _stepsOpen, value, nameof(StepsVisibility), nameof(StepsLabel));
    }

    public Visibility StepsVisibility => _stepsOpen && Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StepsToggleVisibility => Steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string StepsLabel => $"{Steps.Count} {(Steps.Count == 1 ? "step" : "steps")} {(_stepsOpen ? "▾" : "▸")}";

    public void AddSteps(IEnumerable<AgentStepRow> rows)
    {
        foreach (var row in rows) Steps.Add(row);
        while (Steps.Count > StepsKept) Steps.RemoveAt(0);
        Raise(nameof(StepsLabel), nameof(StepsToggleVisibility), nameof(StepsVisibility));
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        // The running turn is told its time ten times a second: only a change is drawn.
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(names);
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A row of a task's steps or plan: its text in its colour.</summary>
public sealed record AgentStepRow(string Text, Brush Brush);

/// <summary>A changed file; the label says how it changed and whether undo can take it back.</summary>
public sealed record AgentChangeRow(string Label, string Path);

/// <summary>A choice the panel offers in a menu or on a card; the window does the work behind it.</summary>
public sealed record PanelAction(string Label, string Hint, Action Run);

/// <summary>A task in the Tasks list: its goal, how it stands and its last words.</summary>
public sealed record TaskRow(string Title, string Preview, string Time, string Status, Brush StatusBrush, AgentTaskRecord Record);

/// <summary>
/// A chat in the Chats list. <see cref="ConversationId"/> is null for the answers an earlier Link kept without their
/// chat (<see cref="IsEarlier"/>) and for a new chat the next message starts.
/// </summary>
public sealed record ChatRow(string Title, string Preview, string Time, string? ConversationId, bool IsCurrent, bool IsEarlier = false)
{
    public FontWeight Weight => IsCurrent ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A usage window as a bar: "Claude · 5 h", how much of it is used, and when it resets.</summary>
public sealed record LimitRow(string Label, double Percent, string Text, Brush Brush, string Hint);
