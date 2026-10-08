using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>
/// A part of the harness as the inspector draws it. The row outlives a refresh: a changed section is put in its place,
/// so that an open text stays open and keeps its place, and an unchanged one is not drawn again.
/// </summary>
public sealed class HarnessRow(HarnessSection section) : INotifyPropertyChanged
{
    private const double MeterWarning = 0.8;
    private bool _detailOpen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HarnessSection Section { get; private set; } = section;

    public string Heading => Section.Title;
    public string Value => Section.Value;
    public string? Note => Section.Note;
    public string? Detail => Section.Detail;
    public Visibility NoteVisibility => Section.Note is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OpenVisibility => Section.FilePath is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ConsoleVisibility => Section.ShowsConsole ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailToggleVisibility => Section.Detail is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailVisibility => _detailOpen && Section.Detail is not null ? Visibility.Visible : Visibility.Collapsed;
    public string DetailLabel => _detailOpen ? "Hide ▾" : "View ▸";

    /// <summary>The context's bar: how much of the limit is used, in percent for the bar; none for a section with no meter.</summary>
    public double MeterPercent => (Section.Meter ?? 0) * 100;
    public Visibility MeterVisibility => Section.Meter is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The bar turns to a warning near the limit, where the agent's CLI compacts.</summary>
    public bool MeterHigh => Section.Meter >= MeterWarning;

    /// <summary>The jellyfish beside a role in the colour of the role; none for a section with no colour.</summary>
    public Brush? RoleBrush => Section.Color is { } color ? RoleBrushes.Of(color) : null;
    public Visibility RoleVisibility => Section.Color is null ? Visibility.Collapsed : Visibility.Visible;

    public bool DetailOpen
    {
        get => _detailOpen;
        set
        {
            _detailOpen = value;
            Raise(nameof(DetailVisibility), nameof(DetailLabel));
        }
    }

    /// <summary>Puts the section as it stands now in the row; the same one changes nothing.</summary>
    public void Update(HarnessSection now)
    {
        if (now == Section) return;
        Section = now;
        Raise(nameof(Value), nameof(Note), nameof(Detail), nameof(NoteVisibility), nameof(OpenVisibility), nameof(ConsoleVisibility),
            nameof(DetailToggleVisibility), nameof(DetailVisibility), nameof(MeterPercent), nameof(MeterVisibility), nameof(MeterHigh),
            nameof(RoleBrush), nameof(RoleVisibility));
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// The Harness Inspector: what the agent really gets for the task (setup, context, rules, role, project memory, task
/// state, skills, the brief, tools, the check and the project's stats), one card each. It only shows; the window builds
/// the view (<see cref="HarnessView"/>) and does the work behind Open and Console. It has no timer: it draws when it is
/// shown a view.
/// </summary>
public partial class HarnessInspector : UserControl
{
    private readonly ObservableCollection<HarnessRow> _rows = [];

    public HarnessInspector()
    {
        InitializeComponent();
        SectionsList.ItemsSource = _rows;
    }

    /// <summary>A file of the harness (the rules, a role, the map, .mcp.json) is to be shown in Explorer.</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>The Console, the raw view of what the agent's CLI got and wrote back, is to be opened.</summary>
    public event Action? ConsoleRequested;

    /// <summary>Export team pack…: the window asks for a file and writes the workspace's roles and skills to it.</summary>
    public event Action? TeamPackExportRequested;

    /// <summary>Import team pack…: the window asks for a file and takes its roles and skills in.</summary>
    public event Action? TeamPackImportRequested;

    /// <summary>What the last export or import did, or why it could not.</summary>
    public void ShowTeamPackStatus(string text)
    {
        PackStatusText.Text = text;
        PackStatusText.Visibility = Visibility.Visible;
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

    private void Open_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HarnessRow { Section.FilePath: { } path } }) OpenFileRequested?.Invoke(path);
    }

    private void Console_OnClick(object sender, RoutedEventArgs e) => ConsoleRequested?.Invoke();

    private void ExportPack_OnClick(object sender, RoutedEventArgs e) => TeamPackExportRequested?.Invoke();

    private void ImportPack_OnClick(object sender, RoutedEventArgs e) => TeamPackImportRequested?.Invoke();

    private void Detail_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HarnessRow row }) row.DetailOpen = !row.DetailOpen;
    }
}
