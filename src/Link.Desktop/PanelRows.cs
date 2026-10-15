using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// What the panel's lists and threads show, one record per row; the panels draw them and the card fills them.

/// <summary>
/// The user's words in a thread: a task's brief, a reply or a correction, a question to the chat. Long words show
/// folded to their first lines; a click shows them all and folds them again.
/// </summary>
public sealed class ThreadUser(string text, string time, string carried = "") : INotifyPropertyChanged
{
    private const int FoldedLines = 8;
    private const int FoldedLength = 600;
    private readonly string? _folded = Fold(text.Trim());
    private bool _open;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text => _open || _folded is null ? text : _folded;
    public string Time => time;
    public string Carried => carried;
    public Visibility CarriedVisibility => Carried.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ToggleVisibility => _folded is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The same words at the same time, when the thread is drawn again.</summary>
    public string Key => time + "\n" + text;
    public string ToggleLabel => _open ? "Show less ▴" : "Show all ▾";

    public bool Open
    {
        get => _open;
        set
        {
            if (_open == value || _folded is null) return;
            _open = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleLabel)));
        }
    }

    // The first lines up to a length, cut at a space; null when the words are short enough to show whole.
    private static string? Fold(string words)
    {
        var lines = words.Split('\n');
        if (lines.Length <= FoldedLines && words.Length <= FoldedLength) return null;
        var start = string.Join('\n', lines.Take(FoldedLines));
        if (start.Length > FoldedLength) start = start[..(start.LastIndexOf(' ', FoldedLength) is > FoldedLength / 2 and var space ? space : FoldedLength)];
        return start.TrimEnd() + "…";
    }
}

/// <summary>A line between the turns of a task: a hand-over, another model, a limit, the task finished.</summary>
public sealed record ThreadNote(string Text, string Time);

/// <summary>A chat's answer in its thread, cut to a glance; Show puts the whole of it in Link's card.</summary>
public sealed record ThreadAnswer(string Text, string Time, ProjectExchange Exchange);

/// <summary>
/// An agent's run in its task's thread: who worked and on what, its steps folded, its answer, the files it changed and
/// what it cost. The running one fills as the events come. <see cref="RoleBrush"/> is the colour of the jellyfish of the
/// specialist the run worked as.
/// </summary>
public sealed class ThreadTurn(string who, string outcome, Brush outcomeBrush, Brush roleBrush) : INotifyPropertyChanged
{
    private const int StepsKept = StepGroup.RowsKept;
    private string _outcome = outcome;
    private Brush _outcomeBrush = outcomeBrush;
    private string _current = string.Empty;
    private string _said = string.Empty;
    private IReadOnlyList<AgentChangeRow> _files = [];
    private string _next = string.Empty;
    private string _stats = string.Empty;
    private bool _stepsOpen;
    private bool _filesOpen;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>"Claude Code · Opus 5.5 · Extra high".</summary>
    public string Who { get; } = who;

    public Brush RoleBrush { get; } = roleBrush;

    /// <summary>The run's steps in order: the agent's words as rows, the tool steps between them as <see cref="StepGroup"/>s.</summary>
    public ObservableCollection<object> Steps { get; } = [];

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
        set => Set(ref _files, value, nameof(Files), nameof(FilesVisibility), nameof(FilesToggleVisibility), nameof(FilesLabel));
    }

    /// <summary>More changed files than this fold into one line, "91 files changed ▸", that opens them: a big change would bury the thread.</summary>
    private const int FilesShown = 8;

    public bool FilesOpen
    {
        get => _filesOpen;
        set => Set(ref _filesOpen, value, nameof(FilesVisibility), nameof(FilesLabel));
    }

    public Visibility FilesVisibility => _files.Count > 0 && (_files.Count <= FilesShown || _filesOpen) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilesToggleVisibility => _files.Count > FilesShown ? Visibility.Visible : Visibility.Collapsed;
    public string FilesLabel => $"{_files.Count} files changed {(_filesOpen ? "▾" : "▸")}";

    /// <summary>The next step the agent ended with, "Next: wire the jump · medium"; empty when it named none.</summary>
    public string Next
    {
        get => _next;
        set => Set(ref _next, value, nameof(Next), nameof(NextVisibility));
    }

    public Visibility NextVisibility => _next.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The run's numbers and check in a line or two.</summary>
    public string Stats
    {
        get => _stats;
        set => Set(ref _stats, value, nameof(Stats), nameof(StatsVisibility));
    }

    public Visibility StatsVisibility => _stats.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The steps are shown; closing them folds their groups, so that they open folded again.</summary>
    public bool StepsOpen
    {
        get => _stepsOpen;
        set
        {
            if (!value) FoldGroups();
            Set(ref _stepsOpen, value, nameof(StepsVisibility), nameof(StepsLabel));
        }
    }

    public Visibility StepsVisibility => _stepsOpen && Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StepsToggleVisibility => Steps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string StepsLabel => $"{StepCount} {(StepCount == 1 ? "step" : "steps")} {(_stepsOpen ? "▾" : "▸")}";

    private int StepCount => Steps.Sum(step => step is StepGroup group ? group.Rows.Count : 1);

    /// <summary>
    /// Rows join the steps: a tool step goes to the group before it, or starts one when the agent spoke in between. While
    /// the steps are shown the newest group is open and the one before it folds; a turn read afterwards has them all folded.
    /// </summary>
    public void AddSteps(IEnumerable<AgentStepRow> rows)
    {
        foreach (var row in rows)
        {
            if (StepGroups.KindOf(row.Text) is null)
            {
                Steps.Add(row);
                continue;
            }

            if (Steps.Count == 0 || Steps[^1] is not StepGroup group)
            {
                FoldGroups();
                group = new StepGroup { Open = _stepsOpen };
                Steps.Add(group);
            }

            group.Add(row);
        }

        // The oldest go first, a whole group at a time; a group that is alone is cut by itself.
        while (StepCount > StepsKept && Steps.Count > 1) Steps.RemoveAt(0);
        Raise(nameof(StepsLabel), nameof(StepsToggleVisibility), nameof(StepsVisibility));
    }

    private void FoldGroups()
    {
        foreach (var group in Steps.OfType<StepGroup>()) group.Open = false;
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

/// <summary>A row of a task's steps: its text in its colour.</summary>
public sealed record AgentStepRow(string Text, Brush Brush);

/// <summary>
/// A step of the agent's plan: its mark (✓ done, ● under way, ○ to do) and the mark's colour, its first sentence and
/// their colour; the rest of its words (<see cref="Detail"/>) open under it on a click. A team's step also names who
/// does it (<see cref="Worker"/>) and the specialist, with the colour of its jellyfish; an agent's own plan has neither.
/// </summary>
public sealed class PlanRow(string mark, Brush markBrush, string title, Brush brush) : INotifyPropertyChanged
{
    private bool _open;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Mark => mark;
    public Brush MarkBrush => markBrush;
    public string Title => title;
    public Brush Brush => brush;

    /// <summary>The words after the first sentence and, for a team's step, why its worker; empty: the row does not open.</summary>
    public string Detail { get; init; } = string.Empty;

    public string Worker { get; init; } = string.Empty;
    public string RoleName { get; init; } = string.Empty;
    public Brush? RoleBrush { get; init; }

    /// <summary>The step's place in the plan, from 0: its worker chip opens the step's menu.</summary>
    public int Index { get; init; }

    public Visibility WorkerVisibility => Worker.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RoleVisibility => RoleName.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string Chevron => Detail.Length == 0 ? string.Empty : _open ? "▾" : "▸";
    public Visibility DetailVisibility => _open ? Visibility.Visible : Visibility.Collapsed;

    public bool Open
    {
        get => _open;
        set
        {
            if (_open == value || Detail.Length == 0) return;
            _open = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chevron)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailVisibility)));
        }
    }
}

/// <summary>
/// The tool steps between two pieces of the agent's words, folded into the line that sums them up ("Ran 3 commands, read
/// 2 files"); a click opens the rows.
/// </summary>
public sealed class StepGroup : INotifyPropertyChanged
{
    public const int RowsKept = 300;
    private bool _open;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AgentStepRow> Rows { get; } = [];

    public string Label => $"{StepGroups.Summarize(Rows.Select(row => row.Text))} {(_open ? "▾" : "▸")}";
    public Visibility RowsVisibility => _open ? Visibility.Visible : Visibility.Collapsed;

    public bool Open
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            Raise(nameof(Label), nameof(RowsVisibility));
        }
    }

    public void Add(AgentStepRow row)
    {
        Rows.Add(row);
        while (Rows.Count > RowsKept) Rows.RemoveAt(0);
        Raise(nameof(Label));
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A changed file; the label says how it changed and whether undo can take it back.</summary>
public sealed record AgentChangeRow(string Label, string Path);

/// <summary>A choice the panel offers in a menu or on a card; the window does the work behind it. A checked one is the current one.</summary>
public sealed record PanelAction(string Label, string Hint, Action Run, bool Checked = false);

/// <summary>A box the panel offers on a card: whether it is ticked, and what ticking or unticking it does.</summary>
public sealed record PanelToggle(string Label, string Hint, bool On, Action<bool> Changed);

/// <summary>
/// A task in the Studio's sidebar: its goal, how it stands and its last words; no record: Link's own run (the map).
/// <see cref="RoleBrush"/> is the colour of its specialist's jellyfish; <see cref="IsOpen"/> lights the row of the task the
/// Studio shows.
/// </summary>
public sealed record TaskRow(string Title, string Preview, string Time, string Status, Brush StatusBrush, Brush RoleBrush, AgentTaskRecord? Record)
{
    public bool IsOpen { get; init; }
}

/// <summary>The jellyfish's brushes: one frozen brush for each colour, shared by every row that has it.</summary>
public static class RoleBrushes
{
    private static readonly ConcurrentDictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Link's own orange: General, the work with no specialist.</summary>
    public static Brush Link => Of(ProjectRoles.LinkColor);

    /// <summary>The brush of a "#RRGGBB" colour, as <see cref="ProjectRoles.ColorOf"/> gives it.</summary>
    public static Brush Of(string hex) => Cache.GetOrAdd(hex, text =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text));
        brush.Freeze();
        return brush;
    });

    /// <summary>
    /// The brush of the specialist <paramref name="roleId"/>, found among <paramref name="roles"/>, which are read only when
    /// there is one to find; Link's orange for General and for a role whose file is gone.
    /// </summary>
    public static Brush For(string? roleId, Lazy<IReadOnlyList<AgentRole>> roles) =>
        roleId is null || roles.Value.FirstOrDefault(role => string.Equals(role.Id, roleId, StringComparison.OrdinalIgnoreCase)) is not { } found
            ? Link
            : Of(ProjectRoles.ColorOf(found, roles.Value));
}

/// <summary>
/// A chat in the Chats list. <see cref="ConversationId"/> is null for the answers an earlier Link kept without their
/// chat (<see cref="IsEarlier"/>) and for a new chat the next message starts.
/// </summary>
public sealed record ChatRow(string Title, string Preview, string Time, string? ConversationId, bool IsCurrent, bool IsEarlier = false)
{
    public FontWeight Weight => IsCurrent ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A usage window as a bar: "Claude · 5 h", how much of it is used, and when it resets; a click asks its agent anew.</summary>
public sealed record LimitRow(string Label, double Percent, string Text, Brush Brush, string Hint, AgentProviderKind Provider);
