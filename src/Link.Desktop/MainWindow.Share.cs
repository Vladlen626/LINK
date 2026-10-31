using System.Windows;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Microsoft.Win32;

namespace Link.Desktop;

// Sharing the harness, in the Harness column's Share card: what the agents learned in the tool packs for a friend's Link,
// the workspace's roles and skills for a teammate, and taking either in. What a file holds or brings is listed first.
public partial class MainWindow
{
    private const string PackFilter = "LINK pack (*.linkpack)|*.linkpack";

    // What the card's list is for: saving what is ticked to a file, or taking it in.
    private Action<IReadOnlyList<SharePick>>? _sharePicked;

    // The task that fits what came in to the project, behind Set up for this project.
    private string? _setUpBrief;

    private void WireShare(StudioWindow studio)
    {
        studio.ShareLearnedRequested += ShareLearned;
        studio.TeamPackExportRequested += ExportTeamPack;
        studio.OpenPackRequested += OpenPack;
        studio.PicksConfirmed += picks => _sharePicked?.Invoke(picks);
        studio.SetUpRequested += () => _ = SetUpPackAsync();
    }

    /// <summary>Share ▾ → What my agents learned: the tool packs' skills and roles beyond Link's own, ticked to go.</summary>
    private void ShareLearned()
    {
        if (_studio is not { } studio) return;
        IReadOnlyList<KnowledgeItem> items;
        try
        {
            items = KnowledgePack.Learned(ToolPacks.Folder, ToolPacks.BuiltInFolder, PersonalWords());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            studio.ShowShareStatus($"The tool packs could not be read: {exception.Message}");
            return;
        }

        if (items.Count == 0)
        {
            studio.ShowShareStatus("Your agents have kept nothing in the tool packs beyond Link's own yet: a lesson gets there with Share with all games after a task.");
            return;
        }

        _sharePicked = picked => SaveLearned([.. items.Where(item => picked.Any(pick => pick.Item == item.Item))]);
        // A line that looks personal leaves its item unticked: the user reads it before it goes.
        studio.ShowSharePicks(
            $"What your agents learned, {Plural(items.Count, "item")} beyond Link's own. An item with a line that looks personal is not ticked: read it first.",
            [.. items.Select(item => new SharePick(item.Item, $"{item.Item} · {(item.State == KnowledgeState.Added ? "new" : "changed")}", item.Description,
                item.Personal.Count == 0 ? null : "Looks personal: " + string.Join("; ", item.Personal)) { Picked = item.Personal.Count == 0 })],
            "Save…");
    }

    private void SaveLearned(IReadOnlyList<KnowledgeItem> items)
    {
        if (_studio is not { } studio) return;
        if (items.Count == 0)
        {
            studio.ShowShareStatus("Nothing was ticked, so no file was written.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Share what my agents learned",
            Filter = PackFilter,
            DefaultExt = TeamPack.Extension,
            FileName = $"skills-{DateTime.Today:yyyy-MM-dd}{TeamPack.Extension}"
        };
        if (dialog.ShowDialog(studio) != true) return;
        try
        {
            KnowledgePack.Export(ToolPacks.Folder, items, dialog.FileName);
            studio.ShowShareStatus($"Saved {Plural(items.Count, "item")} to {dialog.FileName}. A friend takes it with Open a pack… in Link; " +
                                   "without Link, its README says where the skills go.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            studio.ShowShareStatus($"The file could not be written: {exception.Message}");
        }
    }

    /// <summary>
    /// The words that make a line of a shared skill look personal: the user's name, and each project's name and folder
    /// (the default project and short names, which match common words, aside).
    /// </summary>
    private string[] PersonalWords() =>
    [
        .. _projects.Projects
            .SelectMany(project => new[] { project.Name, project.WorkspaceRoot is { } root ? Path.GetFileName(root.TrimEnd('\\', '/')) : null })
            .Append(Environment.UserName)
            .OfType<string>()
            .Where(word => word.Length >= 5 && !string.Equals(word, "General", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>Share ▾ → This workspace's roles and skills: the team pack to a file the user names.</summary>
    private void ExportTeamPack()
    {
        if (_studio is not { } studio) return;
        if (StudioWorkspace() is not { } workspace)
        {
            studio.ShowShareStatus("Choose the workspace folder in the Project tab first.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Share this workspace's roles and skills",
            Filter = PackFilter,
            DefaultExt = TeamPack.Extension,
            FileName = Path.GetFileName(workspace) + TeamPack.Extension
        };
        if (dialog.ShowDialog(studio) != true) return;
        try
        {
            var (roles, skills) = TeamPack.Export(workspace, dialog.FileName);
            studio.ShowShareStatus(roles + skills == 0
                ? "Nothing to share: the workspace has no roles or skills of its own in .agents."
                : $"Saved {Plural(roles, "role")} and {Plural(skills, "skill")} to {dialog.FileName}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            studio.ShowShareStatus($"The pack could not be written: {exception.Message}");
        }
    }

    /// <summary>Open a pack…: a knowledge pack shows what it would change in the tool packs; a team pack goes into the workspace.</summary>
    private void OpenPack()
    {
        if (_studio is not { } studio) return;
        var dialog = new OpenFileDialog { Title = "Open a pack", Filter = PackFilter };
        if (dialog.ShowDialog(studio) != true) return;
        var file = dialog.FileName;
        if (!KnowledgePack.IsOne(file))
        {
            ImportTeamPack(file);
            return;
        }

        try
        {
            var items = KnowledgePack.Preview(file, ToolPacks.Folder);
            var brings = items.Where(item => item.State != KnowledgeState.Same).ToArray();
            if (brings.Length == 0)
            {
                studio.ShowShareStatus("You have all of this pack already.");
                return;
            }

            _sharePicked = picked => TakeLearned(file, picked);
            var same = items.Count - brings.Length;
            studio.ShowSharePicks(
                $"{Path.GetFileName(file)} brings {Plural(brings.Length, "skill or role", "skills or roles")} to your tool packs, shared by all projects" +
                (same > 0 ? $"; {same} you have the same." : "."),
                [.. brings.Select(item => new SharePick(item.Item, $"{item.Item} · {StateWords(item.State)}", item.Description,
                    item.State == KnowledgeState.BothChanged ? "You changed it too: yours stays, the pack's goes beside it for a merge." : null))],
                "Take them");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            studio.ShowShareStatus($"The pack was not opened: {exception.Message}");
        }
    }

    private static string StateWords(KnowledgeState state) => state switch
    {
        KnowledgeState.Added => "new",
        KnowledgeState.Updates => "updates yours",
        KnowledgeState.BothChanged => "both changed",
        _ => "the same"
    };

    private void TakeLearned(string file, IReadOnlyList<SharePick> picked)
    {
        if (_studio is not { } studio) return;
        try
        {
            var import = KnowledgePack.Import(file, ToolPacks.Folder, [.. picked.Select(pick => pick.Item)]);
            var any = import.Taken.Count + import.PutBeside.Count > 0;
            var workspace = StudioProject.WorkspaceRoot is { } root && Directory.Exists(root);
            _setUpBrief = any ? KnowledgePack.SetUpBrief(Path.GetFileNameWithoutExtension(file), ToolPacks.Folder, import) : null;
            studio.ShowShareStatus(
                $"Taken into the tool packs: {Plural(import.Taken.Count, "item")}" +
                (import.PutBeside.Count > 0 ? $", and {import.PutBeside.Count} beside yours in {import.Beside}" : string.Empty) +
                (!any ? "." : workspace ? ". Set up for this project fits them to it." : ". Choose a workspace in the Project tab to set them up for a project."),
                setUp: any && workspace);
            RefreshProjectPanel();
            RefreshStudio();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            studio.ShowShareStatus($"The pack was not taken: {exception.Message}");
        }
    }

    /// <summary>Set up for this project: the task card with the brief that fits what came in; nothing runs before Run.</summary>
    private async Task SetUpPackAsync()
    {
        if (_setUpBrief is not { } brief) return;
        var snapshot = await TakeSnapshotAsync(StudioProject, brief, RequestRoute.Agent, freshFrame: false);
        OpenAgentCard(snapshot, brief);
    }

    /// <summary>A team pack's roles and skills into the workspace; what it has already is replaced only if the user says so.</summary>
    private void ImportTeamPack(string file)
    {
        if (_studio is not { } studio) return;
        if (StudioWorkspace() is not { } workspace)
        {
            studio.ShowShareStatus("Choose the workspace folder in the Project tab first.");
            return;
        }

        // One question for everything the workspace has already, asked at the first.
        bool? replaceThem = null;
        try
        {
            var result = TeamPack.Import(file, workspace, name => replaceThem ??= MessageBox.Show(studio,
                $"The workspace has {name} already, and maybe more of the pack's. Replace the ones it has with the pack's?\n\nNo keeps yours and adds only what is new.",
                "Link · team pack", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
            studio.ShowShareStatus($"Taken: {result.Added.Count} new, {result.Replaced.Count} replaced, {result.Skipped.Count} kept as they were.");
            RefreshProjectPanel();
            RefreshStudio();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            studio.ShowShareStatus($"The pack was not taken: {exception.Message}");
        }
    }
}
