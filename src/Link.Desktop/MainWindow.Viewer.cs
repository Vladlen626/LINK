using System.Windows;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    // The task the viewer's comments go to: the one open on the Studio tab when a file was opened.
    private TaskWork? _viewerTask;

    private void WireViewer()
    {
        Viewer.Closed += () => Viewer.Visibility = Visibility.Collapsed;
        Viewer.CommentsSent += (words, pictures) =>
        {
            if (_viewerTask is not { } work || !_works.Contains(work)) return;
            CloseViewer();
            UserSpoke(work);
            ContinueAgent(work, words, pictures);
        };
        // A file an agent's answer names comes here from any Markdown box in the window.
        AddHandler(MarkdownText.FileRequestedEvent, new EventHandler<FileRequestedEventArgs>((_, e) =>
        {
            if (!File.Exists(e.Path) && !Directory.Exists(e.Path)) return;
            ShowFile(e.Path);
            e.Handled = true;
        }));
    }

    /// <summary>
    /// Shows a file, or a folder's files (a pack's), in Link's viewer over the tab's page. Nothing opened there runs: a
    /// changed script is not to be run by a click.
    /// </summary>
    private void ShowFile(string path)
    {
        AimComments();
        Viewer.Open(path);
        Viewer.Visibility = Visibility.Visible;
    }

    /// <summary>What the Harness column names: a file or a folder, or text Link made (a brief, a check's output).</summary>
    private void OpenHarnessLink(HarnessLink link)
    {
        if (link.Path is { } path)
        {
            ShowFile(path);
        }
        else if (link.Text is { } text)
        {
            Viewer.OpenText(link.Label, "Link's own text, not a file", text);
            Viewer.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The project's memory, the map and the model's notes, as one list in the viewer.</summary>
    private void ShowMemory()
    {
        if (_projects.ActiveProject.WorkspaceRoot is not { } workspace) return;
        var files = ProjectMemory.Files(workspace);
        AimComments();
        Viewer.OpenSet(files, workspace, files.FirstOrDefault());
        Viewer.Visibility = Visibility.Visible;
    }

    /// <summary>Comments can be made on the Studio tab while a task is open there and takes words.</summary>
    private void AimComments()
    {
        _viewerTask = Place.Tab == StudioTab.Studio && _open is { } work && (work.Busy || TakesWords(work)) ? work : null;
        Viewer.CommentOn(_viewerTask, _viewerTask?.Record.Goal);
    }

    private void CloseViewer()
    {
        if (Viewer.Visibility == Visibility.Visible) Viewer.Close();
    }
}
