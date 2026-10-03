using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Microsoft.Win32;

namespace Link.Desktop;

/// <summary>
/// The Project tab beside Link: the project note, the linked chat, the workspace and one history of answers and tasks.
/// It only shows and edits; the card does the work behind each action.
/// </summary>
public partial class ProjectPanel : UserControl
{
    private bool _showing;
    private bool _canSummarize;
    private string? _workspace;

    public ProjectPanel() => InitializeComponent();

    /// <summary>The note was typed: its first line is the focus, the rest what the project stands on.</summary>
    public event Action<string>? NoteChanged;

    /// <summary>The note or the check lost the keyboard: what was typed can be saved.</summary>
    public event Action? EditEnded;

    public event Action? ConversationRequested;

    /// <summary>A new chat was asked for; true: with a summary of the current one.</summary>
    public event Action<bool>? NewChatRequested;

    public event Action? OpenChatRequested;

    /// <summary>A workspace folder was chosen, or null when the user forgot it.</summary>
    public event Action<string?>? WorkspaceChosen;

    /// <summary>The map of the workspace is to be written or brought up to date.</summary>
    public event Action? MapRequested;

    /// <summary>A skill's SKILL.md, to be shown in Explorer.</summary>
    public event Action<string>? SkillRequested;

    /// <summary>The check after edits was typed; empty runs none.</summary>
    public event Action<string>? CheckCommandChanged;

    /// <summary>A line of the history: a chat answer to show, or an agent task to open.</summary>
    public event Action<HistoryRow>? HistorySelected;

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
        var skills = ProjectSkills.In(_workspace);
        SkillsList.ItemsSource = skills;
        SkillsEmptyText.Visibility = skills.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowNoteDate(ProjectState? note) =>
        NoteDateText.Text = note is null ? string.Empty : $"updated {note.UpdatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    /// <param name="canSummarize">The linked chat exists, so it can write a summary for a new one.</param>
    public void ShowConnection(string text, Brush dot, bool canSummarize)
    {
        ChatConnectionText.Text = text;
        ChatStatusDot.Fill = dot;
        _canSummarize = canSummarize;
    }

    public void ShowHistory(IEnumerable<HistoryRow> rows)
    {
        var list = rows.ToList();
        HistoryList.ItemsSource = list;
        HistoryEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>While a request runs the chat stays as it is and its answer holds the card.</summary>
    public void SetBusy(bool busy)
    {
        ConversationButton.IsEnabled = !busy;
        NewChatButton.IsEnabled = !busy;
        HistoryList.IsEnabled = !busy;
    }

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

    private void Conversation_OnClick(object sender, RoutedEventArgs e) => ConversationRequested?.Invoke();

    private void NewChat_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = NewChatButton, Placement = PlacementMode.Bottom };
        var plain = new MenuItem { Header = "New chat" };
        plain.Click += (_, _) => NewChatRequested?.Invoke(false);
        var summarized = new MenuItem
        {
            Header = "New chat with a summary of this one",
            IsEnabled = _canSummarize,
            ToolTip = "The current chat writes a short summary first; it goes with your next message as context"
        };
        summarized.Click += (_, _) => NewChatRequested?.Invoke(true);
        menu.Items.Add(plain);
        menu.Items.Add(summarized);
        menu.IsOpen = true;
    }

    private void OpenChat_OnClick(object sender, RoutedEventArgs e) => OpenChatRequested?.Invoke();

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

    private void HistoryItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HistoryRow row }) HistorySelected?.Invoke(row);
    }
}

/// <summary>
/// A line of the panel's history: a chat answer (<see cref="Exchange"/>) or an agent task (<see cref="Task"/>), with
/// its icon, a glance at it and when it happened.
/// </summary>
public sealed record HistoryRow(
    string Title,
    string Preview,
    string Time,
    string Hint,
    Geometry Icon,
    DateTimeOffset At,
    ProjectExchange? Exchange = null,
    AgentTaskRecord? Task = null);
