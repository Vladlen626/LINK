using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Microsoft.Win32;

namespace Link.Desktop;

/// <summary>
/// The Project tab beside Link: the project note and the workspace with its map, skills, check and version control.
/// It only shows and edits; the card does the work behind each action.
/// </summary>
public partial class ProjectPanel : UserControl
{
    private bool _showing;
    private string? _workspace;

    public ProjectPanel() => InitializeComponent();

    /// <summary>The note was typed: its first line is the focus, the rest what the project stands on.</summary>
    public event Action<string>? NoteChanged;

    /// <summary>The note or the check lost the keyboard: what was typed can be saved.</summary>
    public event Action? EditEnded;

    /// <summary>A workspace folder was chosen, or null when the user forgot it.</summary>
    public event Action<string?>? WorkspaceChosen;

    /// <summary>The map of the workspace is to be written or brought up to date.</summary>
    public event Action? MapRequested;

    /// <summary>A skill's SKILL.md, to be shown in Explorer.</summary>
    public event Action<string>? SkillRequested;

    /// <summary>The check after edits was typed; empty runs none.</summary>
    public event Action<string>? CheckCommandChanged;

    public void ShowProject(LinkProject project)
    {
        _showing = true;
        NoteBox.Text = project.State?.Text ?? string.Empty;
        CheckCommandBox.Text = project.CheckCommand ?? string.Empty;
        _showing = false;
        ShowNoteDate(project.State);
        _workspace = project.WorkspaceRoot;
        // The folder's own name tells it apart; the whole path is in the tooltip.
        WorkspaceText.Text = _workspace is null ? "Not chosen yet"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(_workspace)) is { Length: > 0 } name ? name
            : _workspace;
        WorkspaceText.Foreground = (Brush)FindResource(_workspace is null ? "MutedBrush" : "TextBrush");
        WorkspaceText.ToolTip = _workspace;
        ClearWorkspaceButton.Visibility = _workspace is null ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceTools.Visibility = _workspace is null ? Visibility.Collapsed : Visibility.Visible;
        var map = _workspace is null ? null : new FileInfo(ProjectMap.PathIn(_workspace));
        MapText.Text = map is { Exists: true }
            ? $"{ProjectMap.RelativePath} · {map.LastWriteTime.ToString("d MMM", CultureInfo.InvariantCulture)}"
            : "No map yet";
        MapButton.Content = map is { Exists: true } ? "Update map" : "Write map";
        VcsText.Text = _workspace is null ? string.Empty
            : AgentChanges.IsGit(_workspace) ? "Git: undo puts back what an agent changed"
            : AgentChanges.IsPerforce(_workspace) ? "Perforce: agents open files with p4 edit, and each task's files wait in its own pending changelist in P4V"
            : "No version control: Link copies the folder before an agent's edits";
        var skills = ProjectSkills.In(_workspace);
        SkillsList.ItemsSource = skills;
        SkillsEmptyText.Visibility = skills.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowNoteDate(ProjectState? note) =>
        NoteDateText.Text = note is null ? string.Empty : $"updated {note.UpdatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    public void FocusNote()
    {
        // The panel appears without taking the focus; typing needs it.
        Window.GetWindow(this)?.Activate();
        NoteBox.Focus();
        NoteBox.CaretIndex = NoteBox.Text.Length;
    }

    private void NoteBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        NotePlaceholder.Visibility = NoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_showing) NoteChanged?.Invoke(NoteBox.Text.Trim());
    }

    private void CheckCommandBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        CheckPlaceholder.Visibility = CheckCommandBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_showing) CheckCommandChanged?.Invoke(CheckCommandBox.Text.Trim());
    }

    private void Field_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EditEnded?.Invoke();

    private void ChooseWorkspace_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder this project's work lives in",
            InitialDirectory = _workspace ?? string.Empty
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) WorkspaceChosen?.Invoke(dialog.FolderName);
    }

    private void ClearWorkspace_OnClick(object sender, RoutedEventArgs e) => WorkspaceChosen?.Invoke(null);

    private void Map_OnClick(object sender, RoutedEventArgs e) => MapRequested?.Invoke();

    private void Skill_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectSkill skill) SkillRequested?.Invoke(skill.FilePath);
    }
}
