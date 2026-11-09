using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Link.Desktop.Services;

namespace Link.Desktop;

/// <summary>A file in the viewer's list: its name, its folder within what was opened, whether it is the one shown.</summary>
public sealed record ViewerFile(string Path, string Name, string Folder, bool IsOpen)
{
    public Visibility FolderVisibility => Folder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>A comment in the viewer's list: its number (its mark on a picture) and where it is.</summary>
public sealed record ViewerComment(int Number, FileComment Comment, string Where);

/// <summary>How the viewer shows a file, by its extension.</summary>
public enum ViewKind
{
    Markdown,
    Picture,
    Model,
    Text
}

/// <summary>
/// Link's own viewer, over the tab's page: Markdown as a document, a picture beside the last commit's when it changed, an
/// OBJ or STL model to turn, other text as it is. A folder or a set of files lists them on the left. Opening a file here
/// never runs it; Explorer is one click away for anything else. Opened from a task, it takes comments on picked words, a
/// point of a picture or a whole file, and sends them to the task's agent at once.
/// </summary>
public partial class FileViewer : UserControl
{
    private static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico"];
    private static readonly string[] Skipped = [".git", ".vs", ".idea", "bin", "obj", "node_modules", "__pycache__", "Library", "Temp"];

    // More text than this is a log or a dump: its start is shown, the rest is a click on Explorer away.
    private const int MaxTextChars = 400_000;
    private const int MaxFiles = 400;

    private IReadOnlyList<string> _files = [];
    private string? _root;
    private string? _file;
    private CancellationTokenSource? _loading;

    // The task the comments go to (null: none can be made), the comments kept for it, and where the next one goes.
    private object? _target;
    private readonly List<FileComment> _comments = [];
    private (string Place, (double X, double Y)? Spot) _aim = (string.Empty, null);
    private Action? _drawMarks;

    public FileViewer()
    {
        InitializeComponent();
    }

    /// <summary>Back or Esc: the window hides the viewer.</summary>
    public event Action? Closed;

    /// <summary>Send: the comments as the task's next words, and the pictures they are on.</summary>
    public event Action<string, IReadOnlyList<string>>? CommentsSent;

    /// <summary>
    /// The task comments go to, named by its goal; null takes the comments away. Comments not sent yet stay with their
    /// task while the viewer is closed and opened again on it.
    /// </summary>
    public void CommentOn(object? task, string? goal)
    {
        if (!ReferenceEquals(task, _target)) _comments.Clear();
        _target = task;
        CommentBar.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        SendCommentsButton.ToolTip = $"All the comments go to “{goal}” as your next words: its agent answers each";
        ShowComments();
    }

    public static ViewKind KindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)) return ViewKind.Markdown;
        if (Pictures.Contains(extension, StringComparer.OrdinalIgnoreCase)) return ViewKind.Picture;
        return MeshFile.CanRead(path) ? ViewKind.Model : ViewKind.Text;
    }

    /// <summary>A file, or a folder with its files listed (a pack, a memory folder); the folder's main file comes first.</summary>
    public void Open(string path)
    {
        if (!Directory.Exists(path))
        {
            OpenSet([path], null, path);
            return;
        }

        var files = FilesIn(path);
        OpenSet(files, path, files.FirstOrDefault(IsMain) ?? files.FirstOrDefault(file => KindOf(file) == ViewKind.Markdown) ?? files.FirstOrDefault());
    }

    /// <summary>Several files under one list, such as a project's memory; <paramref name="root"/> names their folders.</summary>
    public void OpenSet(IReadOnlyList<string> files, string? root, string? first)
    {
        _files = files;
        _root = root;
        FilesColumn.Width = files.Count > 1 ? new GridLength(240) : new GridLength(0);
        if (first is null)
        {
            _file = root;
            TitleText.Text = Path.GetFileName(root?.TrimEnd('\\', '/')) ?? string.Empty;
            WhereText.Text = root ?? string.Empty;
            FilesList.ItemsSource = null;
            Body.Child = Note("Nothing here yet");
            return;
        }

        Show(first);
    }

    private static bool IsMain(string file) =>
        Path.GetFileName(file) is { } name && (name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) || name.Equals("README.md", StringComparison.OrdinalIgnoreCase));

    // A folder's files, its own first, without build output and the like.
    private static IReadOnlyList<string> FilesIn(string folder)
    {
        var files = new List<string>();
        var folders = new Queue<string>([folder]);
        try
        {
            while (folders.Count > 0 && files.Count < MaxFiles)
            {
                var next = folders.Dequeue();
                files.AddRange(Directory.EnumerateFiles(next).Order(StringComparer.OrdinalIgnoreCase).Take(MaxFiles - files.Count));
                foreach (var child in Directory.EnumerateDirectories(next).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Skipped.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) folders.Enqueue(child);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What could be listed is shown.
        }

        return files;
    }

    private async void Show(string file)
    {
        _file = file;
        _drawMarks = null;
        Aim(string.Empty, null);
        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var cancellation = _loading.Token;

        TitleText.Text = Path.GetFileName(file);
        WhereText.Text = Path.GetDirectoryName(file) ?? string.Empty;
        FilesList.ItemsSource = _files.Count > 1 ? _files.Select(path => Row(path, path == file)).ToArray() : null;
        Body.Child = Note("Opening…");
        try
        {
            var view = await ViewAsync(file, cancellation);
            if (!cancellation.IsCancellationRequested) Body.Child = view;
        }
        catch (OperationCanceledException)
        {
            // Another file was chosen meanwhile.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            if (!cancellation.IsCancellationRequested) Body.Child = Note($"Link could not read it: {exception.Message}");
        }
    }

    private ViewerFile Row(string path, bool open)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var shown = _root is not null && folder.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(_root, folder).Replace('\\', '/')
            : Path.GetFileName(folder);
        return new ViewerFile(path, Path.GetFileName(path), shown == "." ? string.Empty : shown, open);
    }

    private async Task<UIElement> ViewAsync(string file, CancellationToken cancellation)
    {
        if (!File.Exists(file)) return Note("The file is gone");
        switch (KindOf(file))
        {
            case ViewKind.Markdown:
                var markdown = await File.ReadAllTextAsync(file, cancellation);
                var box = new RichTextBox { Style = (Style)FindResource("ReadRichTextStyle"), FontSize = 14 };
                MarkdownText.SetSource(box, markdown);
                box.SelectionChanged += (_, _) => AimAt(box.Selection.Text, markdown, null);
                return Scrolled(new Border { MaxWidth = 860, Margin = new Thickness(28, 20, 28, 28), Child = box });
            case ViewKind.Picture:
                var now = await Task.Run(() => Picture(File.ReadAllBytes(file)), cancellation);
                var before = await AgentChanges.CommittedAsync(file, cancellation) is { } bytes ? await Task.Run(() => Picture(bytes), cancellation) : null;
                return before is null ? PictureView(now, null, file) : BeforeAfter(before, now, file);
            case ViewKind.Model:
                var mesh = await Task.Run(() => Geometry(file), cancellation);
                return mesh is null ? Note("No triangles Link can read in it (OBJ and STL shapes, under 200 MB)") : ModelView(mesh);
            default:
                var text = await Task.Run(() => Text(file), cancellation);
                if (text is null) return Note("Link does not show this kind of file: Show in Explorer opens its folder");
                var lines = new TextBox
                {
                        Style = (Style)FindResource("BareTextBoxStyle"),
                        IsReadOnly = true,
                    Text = text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12.5,
                    Foreground = (Brush)FindResource("TextBrush"),
                    Padding = new Thickness(20, 14, 20, 14),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                // Lines do not wrap here, so the box's line is the file's.
                lines.SelectionChanged += (_, _) => AimAt(lines.SelectedText, null, lines.GetLineIndexFromCharacterIndex(lines.SelectionStart) + 1);
                return lines;
        }
    }

    // Text as it is, null when the file is not text: a zero byte in its start says so.
    private static string? Text(string file)
    {
        using var stream = File.OpenRead(file);
        var start = new byte[Math.Min(8192, stream.Length)];
        stream.ReadExactly(start);
        if (Array.IndexOf(start, (byte)0) >= 0) return null;
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxTextChars];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return reader.EndOfStream ? new string(buffer, 0, read) : new string(buffer, 0, read) + "\n\n… the rest is longer than Link shows";
    }

    private static BitmapSource Picture(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // Fitted to the page, never blown up past its own size.
    private static Image PictureImage(BitmapSource picture) =>
        new() { Source = picture, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(16) };

    private UIElement PictureView(BitmapSource picture, string? label, string? file)
    {
        var panel = new DockPanel();
        var caption = new TextBlock
        {
            Text = label is null ? $"{picture.PixelWidth} × {picture.PixelHeight}" : $"{label} · {picture.PixelWidth} × {picture.PixelHeight}",
            Style = (Style)FindResource("SecondaryTextStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(caption, Dock.Top);
        panel.Children.Add(caption);
        panel.Children.Add(file is null ? PictureImage(picture) : Commentable(picture, file));
        return panel;
    }

    /// <summary>The picture comments are on: a click picks a point, and each comment on it is a numbered mark.</summary>
    private Grid Commentable(BitmapSource picture, string file)
    {
        var image = PictureImage(picture);
        image.Margin = new Thickness(0);
        var marks = new Canvas { IsHitTestVisible = false };
        // Centred, the grid takes the fitted picture's size, so the marks lie on its pixels.
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
        if (_target is not null) host.Cursor = Cursors.Cross;
        host.Children.Add(image);
        host.Children.Add(marks);

        void Draw()
        {
            marks.Children.Clear();
            var spots = _comments.Select((comment, index) => (comment, Label: (index + 1).ToString()))
                .Where(each => each.comment.Path == file && each.comment.Spot is not null)
                .Select(each => (Spot: each.comment.Spot!.Value, each.Label)).ToList();
            if (_aim.Spot is { } next) spots.Add((next, "+"));
            foreach (var (spot, label) in spots)
            {
                var mark = Mark(label);
                Canvas.SetLeft(mark, spot.X * image.ActualWidth - 10);
                Canvas.SetTop(mark, spot.Y * image.ActualHeight - 10);
                marks.Children.Add(mark);
            }
        }

        _drawMarks = Draw;
        image.SizeChanged += (_, _) => Draw();
        host.MouseLeftButtonDown += (_, e) =>
        {
            if (_target is null || image.ActualWidth <= 0 || image.ActualHeight <= 0) return;
            var at = e.GetPosition(image);
            var spot = (Math.Clamp(at.X / image.ActualWidth, 0, 1), Math.Clamp(at.Y / image.ActualHeight, 0, 1));
            Aim(FileComments.Point(spot.Item1, spot.Item2, picture.PixelWidth, picture.PixelHeight), spot);
            CommentBox.Focus();
        };
        return host;
    }

    private Border Mark(string label) => new()
    {
        Width = 20,
        Height = 20,
        CornerRadius = new CornerRadius(10),
        Background = (Brush)FindResource("AccentBrush"),
        BorderBrush = Brushes.White,
        BorderThickness = new Thickness(1.5),
        Child = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private UIElement BeforeAfter(BitmapSource before, BitmapSource now, string file)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var after = PictureView(now, "Now", file);
        Grid.SetColumn(after, 1);
        grid.Children.Add(PictureView(before, "Before · the last commit", null));
        grid.Children.Add(after);
        return grid;
    }

    private static MeshGeometry3D? Geometry(string file)
    {
        if (MeshFile.Read(file) is not { } mesh) return null;
        var geometry = new MeshGeometry3D { Positions = new Point3DCollection(mesh.Positions), TriangleIndices = new Int32Collection(mesh.Triangles) };
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The model lit from the front, centred and turned a little; drag turns it, the wheel brings it closer.</summary>
    private UIElement ModelView(MeshGeometry3D geometry)
    {
        var bounds = geometry.Bounds;
        var center = new Vector3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        var radius = Math.Max(1e-6, new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2);
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xC4, 0xCC, 0xD8)));
        material.Freeze();
        var turn = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 30);
        var tilt = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 15);
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-center));
        transform.Children.Add(new RotateTransform3D(turn));
        transform.Children.Add(new RotateTransform3D(tilt));
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(0x50, 0x50, 0x58)));
        scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.6, -0.8, -1)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0x60, 0x60, 0x68), new Vector3D(0.6, 0.5, 1)));
        scene.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material, Transform = transform });

        var camera = new PerspectiveCamera(new Point3D(0, 0, radius * 2.6), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 40)
        {
            NearPlaneDistance = radius / 100,
            FarPlaneDistance = radius * 100
        };
        var viewport = new Viewport3D { Camera = camera, ClipToBounds = true };
        viewport.Children.Add(new ModelVisual3D { Content = scene });

        // The viewport takes the mouse only on the model: a clear surface around it takes it anywhere.
        var surface = new Border { Background = Brushes.Transparent, Child = viewport, Cursor = Cursors.SizeAll };
        Point? last = null;
        surface.MouseLeftButtonDown += (_, e) =>
        {
            last = e.GetPosition(surface);
            surface.CaptureMouse();
        };
        surface.MouseMove += (_, e) =>
        {
            if (last is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
            var to = e.GetPosition(surface);
            turn.Angle += (to.X - from.X) * 0.5;
            tilt.Angle += (to.Y - from.Y) * 0.5;
            last = to;
        };
        surface.MouseLeftButtonUp += (_, _) =>
        {
            last = null;
            surface.ReleaseMouseCapture();
        };
        surface.MouseWheel += (_, e) =>
        {
            var distance = camera.Position.Z * (e.Delta > 0 ? 0.85 : 1 / 0.85);
            camera.Position = new Point3D(0, 0, Math.Clamp(distance, radius * 0.3, radius * 20));
            e.Handled = true;
        };

        var panel = new DockPanel();
        var caption = new TextBlock
        {
            Text = $"{geometry.TriangleIndices.Count / 3:N0} triangles · drag to turn, wheel to come closer",
            Style = (Style)FindResource("SecondaryTextStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(caption, Dock.Top);
        panel.Children.Add(caption);
        panel.Children.Add(surface);
        return panel;
    }

    private static ScrollViewer Scrolled(UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private TextBlock Note(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("SecondaryTextStyle"),
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24)
    };

    private void File_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewerFile row } && row.Path != _file) Show(row.Path);
    }

    private void Back_OnClick(object sender, RoutedEventArgs e) => Close();

    // Picked words, or none: the whole file.
    private void AimAt(string words, string? source, int? line) =>
        Aim(words.Trim().Length == 0 ? string.Empty : FileComments.Picked(source, words, line), null);

    private void Aim(string place, (double X, double Y)? spot)
    {
        _aim = (place, spot);
        var name = Path.GetFileName(_file) ?? string.Empty;
        var hint = _file is null ? string.Empty : KindOf(_file) switch
        {
            ViewKind.Markdown or ViewKind.Text => " · pick words to comment on them",
            ViewKind.Picture => " · click the picture to comment on a point",
            _ => string.Empty
        };
        PlaceText.Text = place.Length > 0 ? $"On {name}, {place}" : $"On the whole of {name}{hint}";
        _drawMarks?.Invoke();
    }

    private void ShowComments()
    {
        CommentsList.ItemsSource = _comments.Select((comment, index) => new ViewerComment(index + 1, comment,
            Path.GetFileName(comment.Path) + (comment.Place.Length > 0 ? ", " + comment.Place : " · the whole file"))).ToArray();
        var count = _comments.Count;
        SendCommentsButton.Content = count > 1 ? $"Send {count} to the task" : "Send to the task";
        SendCommentsButton.IsEnabled = count > 0 || CommentBox.Text.Trim().Length > 0;
        AddCommentButton.IsEnabled = CommentBox.Text.Trim().Length > 0;
        _drawMarks?.Invoke();
    }

    // The comment in the box joins the list on its place; the next one starts on the whole file again.
    private bool AddComment()
    {
        if (_file is null || CommentBox.Text.Trim() is not { Length: > 0 } text) return false;
        _comments.Add(new FileComment(_file, _aim.Place, text, _aim.Spot));
        CommentBox.Clear();
        Aim(string.Empty, null);
        ShowComments();
        return true;
    }

    private void AddComment_OnClick(object sender, RoutedEventArgs e) => AddComment();

    private void SendComments_OnClick(object sender, RoutedEventArgs e)
    {
        AddComment();
        if (_comments.Count == 0) return;
        var pictures = _comments.Select(comment => comment.Path).Where(path => KindOf(path) == ViewKind.Picture && File.Exists(path)).Distinct().ToArray();
        var words = FileComments.Message(_comments);
        _comments.Clear();
        ShowComments();
        CommentsSent?.Invoke(words, pictures);
    }

    private void RemoveComment_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ViewerComment row }) return;
        _comments.Remove(row.Comment);
        ShowComments();
    }

    private void CommentBox_OnTextChanged(object sender, TextChangedEventArgs e) => ShowComments();

    // Enter keeps the comment, Ctrl+Enter sends them all, Shift+Enter is a new line. Esc closes the viewer: the comments stay with the task.
    private void CommentBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) SendComments_OnClick(sender, e);
        else AddComment();
        e.Handled = true;
    }

    /// <summary>Stops a file still loading and leaves the page to the tab.</summary>
    public void Close()
    {
        _loading?.Cancel();
        Body.Child = null;
        Closed?.Invoke();
    }

    private void Explorer_OnClick(object sender, RoutedEventArgs e)
    {
        if (_file is null) return;
        var target = File.Exists(_file) ? $"/select,\"{_file}\"" : $"\"{(Directory.Exists(_file) ? _file : Path.GetDirectoryName(_file))}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true })?.Dispose();
    }
}
