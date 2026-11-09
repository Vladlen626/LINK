using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// A part of the harness as the inspector draws it. The row outlives a refresh: a changed section is put in its place,
/// and an unchanged one is not drawn again.
/// </summary>
public sealed class HarnessRow(HarnessSection section) : INotifyPropertyChanged
{
    private const double MeterWarning = 0.8;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HarnessSection Section { get; private set; } = section;

    public string Heading => Section.Title;
    public string Value => Section.Value;
    public string? Note => Section.Note;
    public Visibility NoteVisibility => Section.Note is null ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<HarnessLinkRow> Links => [.. Section.Links.Select(link => new HarnessLinkRow(link))];
    public Visibility LinksVisibility => Section.Links.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The context's bar: how much of the limit is used, in percent for the bar; none for a section with no meter.</summary>
    public double MeterPercent => (Section.Meter ?? 0) * 100;
    public Visibility MeterVisibility => Section.Meter is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The bar turns to a warning near the limit, where the agent's CLI compacts.</summary>
    public bool MeterHigh => Section.Meter >= MeterWarning;

    /// <summary>Puts the section as it stands now in the row; the same one changes nothing.</summary>
    public void Update(HarnessSection now)
    {
        if (now == Section) return;
        Section = now;
        Raise(nameof(Value), nameof(Note), nameof(NoteVisibility), nameof(Links), nameof(LinksVisibility),
            nameof(MeterPercent), nameof(MeterVisibility), nameof(MeterHigh));
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A line of a card: what opens in the viewer, or only says; a role's jellyfish in the colour of the role.</summary>
public sealed class HarnessLinkRow(HarnessLink link)
{
    public HarnessLink Link { get; } = link;
    public string Label => Link.Label;
    public string NoteText => Link.Note is null ? string.Empty : " · " + Link.Note;
    public bool Opens => Link.Path is not null || Link.Text is not null;
    public string? Tip => Opens ? "Opens in Link's viewer" : null;
    public Brush? RoleBrush => Link.Color is { } color ? RoleBrushes.Of(color) : null;
    public Visibility RoleVisibility => Link.Color is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// One line of what a shared file will hold or what an opened one brings: <see cref="Item"/> goes when ticked. One that
/// cannot be picked (already the same) is only shown.
/// </summary>
public sealed class SharePick(string item, string title, string note, string? warning, bool canPick = true)
{
    public string Item { get; } = item;
    public string Title { get; } = title;
    public string Note { get; } = note;
    public string? Warning { get; } = warning;
    public bool CanPick { get; } = canPick;
    public bool Picked { get; set; } = canPick;
    public Visibility WarningVisibility => Warning is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// The Harness Inspector: what the agent really gets for the task (the agent, its context, its instructions, what it
/// knows of the project, what it may do, the check and how the runs went), one card each, every file and text in them a
/// click from Link's viewer. It only shows; the window builds the view (<see cref="HarnessView"/>) and opens what is
/// clicked. It has no timer: it draws when it is shown a view.
/// </summary>
public partial class HarnessInspector : UserControl
{
    private readonly ObservableCollection<HarnessRow> _rows = [];

    public HarnessInspector()
    {
        InitializeComponent();
        SectionsList.ItemsSource = _rows;
    }

    /// <summary>A file or a text of the harness (the rules, a role, a skill, the brief…) is to be shown in the viewer.</summary>
    public event Action<HarnessLink>? LinkOpened;

    /// <summary>Share ▾ → What my agents learned: the window lists what the tool packs hold beyond Link's own.</summary>
    public event Action? ShareLearnedRequested;

    /// <summary>Share ▾ → This workspace's roles and skills: the window asks for a file and writes the team pack to it.</summary>
    public event Action? TeamPackExportRequested;

    /// <summary>Open a pack…: the window asks for a file and shows what it brings, or takes a team pack in.</summary>
    public event Action? OpenPackRequested;

    /// <summary>The shown list's button: the ticked items go.</summary>
    public event Action<IReadOnlyList<SharePick>>? PicksConfirmed;

    /// <summary>Set up for this project: a task card that fits what came in to the project.</summary>
    public event Action? SetUpRequested;

    /// <summary>Lists what a file will hold or what one brings, under <paramref name="heading"/>, with <paramref name="confirm"/> to go on.</summary>
    public void ShowPicks(string heading, IReadOnlyList<SharePick> picks, string confirm)
    {
        PicksHeadingText.Text = heading;
        PicksList.ItemsSource = picks;
        ConfirmPicksText.Text = confirm;
        PicksPanel.Visibility = Visibility.Visible;
        PackStatusText.Visibility = Visibility.Collapsed;
        SetUpButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>What the last share or opening did, or why it could not; <paramref name="setUp"/> offers Set up for this project.</summary>
    public void ShowShareStatus(string text, bool setUp = false)
    {
        PicksPanel.Visibility = Visibility.Collapsed;
        PicksList.ItemsSource = null;
        PackStatusText.Text = text;
        PackStatusText.Visibility = Visibility.Visible;
        SetUpButton.Visibility = setUp ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Draws the view. The same sections again change nothing; a changed one is changed in its place.</summary>
    public void Show(HarnessView view)
    {
        var sections = view.Sections;
        if (_rows.Count == sections.Count && _rows.Zip(sections).All(pair => pair.First.Section.Title == pair.Second.Title))
        {
            foreach (var (row, section) in _rows.Zip(sections)) row.Update(section);
            return;
        }

        _rows.Clear();
        foreach (var section in sections) _rows.Add(new HarnessRow(section));
    }

    private void Link_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HarnessLinkRow { Opens: true } row }) LinkOpened?.Invoke(row.Link);
    }

    private void Share_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = MenuPlacement.Under((FrameworkElement)sender);
        var learned = new MenuItem
        {
            Header = "What my agents learned…",
            ToolTip = "The tool packs' skills and roles your agents changed or added, for a friend's Link (or Claude Code and Codex without it)"
        };
        learned.Click += (_, _) => ShareLearnedRequested?.Invoke();
        var team = new MenuItem
        {
            Header = "This workspace's roles and skills…",
            ToolTip = "The workspace's .agents/roles and .agents/skills, for a teammate's copy of the same project"
        };
        team.Click += (_, _) => TeamPackExportRequested?.Invoke();
        menu.Items.Add(learned);
        menu.Items.Add(team);
        menu.IsOpen = true;
    }

    private void OpenPack_OnClick(object sender, RoutedEventArgs e) => OpenPackRequested?.Invoke();

    private void ConfirmPicks_OnClick(object sender, RoutedEventArgs e)
    {
        if (PicksList.ItemsSource is IEnumerable<SharePick> picks) PicksConfirmed?.Invoke([.. picks.Where(pick => pick.CanPick && pick.Picked)]);
    }

    private void CancelPicks_OnClick(object sender, RoutedEventArgs e) => ShowShareStatus("Nothing was shared or taken.");

    private void SetUp_OnClick(object sender, RoutedEventArgs e) => SetUpRequested?.Invoke();
}
