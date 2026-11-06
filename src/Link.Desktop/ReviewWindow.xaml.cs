using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>A line of the diff as the review draws it, with the user's comment on it.</summary>
public sealed class ReviewRow(DiffFile file, DiffLine line) : INotifyPropertyChanged
{
    private string? _comment;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiffFile File { get; } = file;
    public DiffLine Line { get; } = line;
    public DiffLineKind Kind => Line.Kind;
    public string OldNumber => Line.Old?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    public string NewNumber => Line.New?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    public string Sign => Line.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "−", _ => string.Empty };

    // A tab is four spaces: the box draws one as wide as it likes.
    public string Text => Line.Text.Replace("\t", "    ");

    public string? Comment
    {
        get => _comment;
        set
        {
            _comment = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Comment)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommentVisibility)));
        }
    }

    public Visibility CommentVisibility => _comment is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A file in the review's list: its name and folder, its numbers and how many comments it has.</summary>
public sealed class ReviewFileRow(DiffFile file) : INotifyPropertyChanged
{
    private int _comments;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiffFile File { get; } = file;
    public string Path => File.Path;
    public string Name => System.IO.Path.GetFileName(File.Path.Replace('\\', '/').TrimEnd('/')) is { Length: > 0 } name ? name : File.Path;
    public string Folder => System.IO.Path.GetDirectoryName(File.Path.Replace('\\', '/'))?.Replace('\\', '/') ?? string.Empty;
    public string AddedText => File.Added > 0 ? $"+{File.Added}" : string.Empty;
    public string RemovedText => File.Removed > 0 ? $"−{File.Removed}" : string.Empty;
    public Visibility CommentsVisibility => _comments > 0 ? Visibility.Visible : Visibility.Collapsed;

    public int Comments
    {
        get => _comments;
        set
        {
            _comments = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Comments)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommentsVisibility)));
        }
    }
}

/// <summary>
/// The review of an agent's changes: the files on the left, the chosen one's diff on the right, and a comment on any line
/// or on the whole change. <see cref="Review"/> is what Send review gives the agent; null when the window was closed
/// without it. It only shows and collects: the window that opens it reads the diff and sends the words.
/// </summary>
public partial class ReviewWindow : Window
{
    private readonly ReviewFileRow[] _files;
    private readonly Dictionary<DiffFile, ReviewRow[]> _rows = [];
    private ReviewRow? _line;
    private string? _whole;
    private bool _sent;

    public ReviewWindow(string title, IReadOnlyList<DiffFile> files)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.PaintFrame(this);
        TitleText.Text = title;
        _files = [.. files.Select(file => new ReviewFileRow(file))];
        FilesList.ItemsSource = _files;
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            FilesList.SelectedIndex = _files.Length > 0 ? 0 : -1;
            ShowPlace();
        };
    }

    public string? Review { get; private set; }

    private IEnumerable<ReviewRow> Commented => _files.SelectMany(file => RowsOf(file.File)).Where(row => row.Comment is not null);

    private int CommentCount => Commented.Count() + (_whole is null ? 0 : 1);

    private ReviewRow[] RowsOf(DiffFile file)
    {
        if (!_rows.TryGetValue(file, out var rows)) _rows[file] = rows = [.. file.Lines.Select(line => new ReviewRow(file, line))];
        return rows;
    }

    private void Files_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilesList.SelectedItem is not ReviewFileRow file) return;
        LinesList.ItemsSource = RowsOf(file.File);
        Choose(null);
    }

    private void Lines_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LinesList.SelectedItem is ReviewRow row) Choose(row);
    }

    /// <summary>
    /// The comment box turns to a line, or to the whole change (null), with what was written on it; what the box held
    /// stays on its own place first.
    /// </summary>
    private void Choose(ReviewRow? row)
    {
        Save();
        _line = row;
        if (row is null) LinesList.SelectedItem = null;
        CommentBox.Text = row is null ? _whole ?? string.Empty : row.Comment ?? string.Empty;
        ShowPlace();
        if (row is not null)
        {
            CommentBox.Focus();
            CommentBox.CaretIndex = CommentBox.Text.Length;
        }
    }

    private void ShowPlace()
    {
        PlaceText.Text = _line is { } row
            ? "On " + DiffReview.Place(new ReviewComment(row.File.Path, row.Line, string.Empty)) + (row.Text.Trim().Length > 0 && row.Kind is not DiffLineKind.Hunk and not DiffLineKind.Note ? "  ·  " + row.Text.Trim() : string.Empty)
            : "On the whole change";
        WholeButton.Visibility = _line is null ? Visibility.Collapsed : Visibility.Visible;
        var count = CommentCount;
        CountText.Text = count == 0 ? "No comments yet" : count == 1 ? "1 comment" : $"{count} comments";
        SendButton.IsEnabled = count > 0 || CommentBox.Text.Trim().Length > 0;
    }

    /// <summary>The box's words go on their place; empty words take the comment away.</summary>
    private void Save()
    {
        var text = CommentBox.Text.Trim();
        if (_line is { } row)
        {
            row.Comment = text.Length == 0 ? null : text;
            var file = _files.First(each => each.File == row.File);
            file.Comments = RowsOf(file.File).Count(each => each.Comment is not null);
        }
        else
        {
            _whole = text.Length == 0 ? null : text;
        }

        ShowPlace();
    }

    private void CommentBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save();
            LinesList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _line is not null)
        {
            Choose(null);
            e.Handled = true;
        }
    }

    private void Save_OnClick(object sender, RoutedEventArgs e) => Save();

    private void Whole_OnClick(object sender, RoutedEventArgs e) => Choose(null);

    private void Send_OnClick(object sender, RoutedEventArgs e)
    {
        // Words left in the box count, as if Add comment was pressed.
        Save();
        if (CommentCount == 0) return;
        var comments = new List<ReviewComment>();
        if (_whole is not null) comments.Add(new ReviewComment(null, null, _whole));
        comments.AddRange(Commented.Select(row => new ReviewComment(row.File.Path, row.Line, row.Comment!)));
        Review = DiffReview.Message(comments);
        _sent = true;
        Close();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var count = CommentCount;
        if (_sent || count == 0) return;
        e.Cancel = MessageBox.Show(this, $"Close the review and drop {(count == 1 ? "its comment" : $"its {count} comments")}?", "Link · review",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes;
    }
}
