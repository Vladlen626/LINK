using System.Globalization;

namespace Link.Desktop.Services;

/// <summary>A file an agent changed. <see cref="Undoable"/>: it was clean before the task, so undoing loses nothing of the user's.</summary>
public sealed record AgentChange(string Path, string Kind, bool Undoable);

/// <summary>
/// What a workspace looked like before a task: in git, its uncommitted files with a fingerprint each; without git,
/// the copy made before Do, if any.
/// </summary>
public sealed record WorkspaceBefore(string Workspace, bool IsGit, IReadOnlyDictionary<string, string> Dirty, string? Backup, DateTime StartedUtc);

/// <summary>
/// Finds what an agent changed and puts it back. In git: <c>git status --porcelain</c> before and after, and undo only
/// for files that were clean before. Without git: a copy of the workspace before Do, compared and restored file by file.
/// Nothing is undone or restored without the user's yes, asked by the caller.
/// </summary>
public sealed class AgentChanges
{
    private const int BackupsKept = 5;
    // A workspace bigger than this is not copied: the copy would take too long and too much disk.
    private const long BackupLimit = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "__pycache__",
        // Unity and Unreal rebuild these themselves.
        "Library", "Temp", "Intermediate", "Saved", "DerivedDataCache", "Binaries"
    };

    private readonly string _backupsRoot;

    public AgentChanges(string backupsRoot) => _backupsRoot = backupsRoot;

    public async Task<WorkspaceBefore> CaptureAsync(string workspace, bool backup, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var status = await GitStatusAsync(workspace, cancellationToken);
        if (status is not null)
        {
            var dirty = status.ToDictionary(entry => entry.Path, entry => Fingerprint(entry.Path), StringComparer.OrdinalIgnoreCase);
            return new WorkspaceBefore(workspace, true, dirty, null, started);
        }

        var copy = backup ? await Task.Run(() => Backup(workspace), cancellationToken) : null;
        return new WorkspaceBefore(workspace, false, new Dictionary<string, string>(), copy, started);
    }

    /// <summary>The files changed since <paramref name="before"/>; without git and without a copy, none can be told.</summary>
    public async Task<IReadOnlyList<AgentChange>> CompareAsync(WorkspaceBefore before, CancellationToken cancellationToken)
    {
        if (before.IsGit)
        {
            var after = await GitStatusAsync(before.Workspace, cancellationToken) ?? [];
            return after
                .Where(entry => !before.Dirty.TryGetValue(entry.Path, out var print) || print != Fingerprint(entry.Path))
                .Select(entry => new AgentChange(entry.Path, entry.Code, !before.Dirty.ContainsKey(entry.Path)))
                .ToArray();
        }

        return before.Backup is null ? [] : await Task.Run(() => CompareWithBackup(before), cancellationToken);
    }

    /// <summary>Puts back the undoable changes: a new file is deleted, a changed or deleted one comes back from HEAD.</summary>
    public async Task UndoAsync(WorkspaceBefore before, IEnumerable<AgentChange> changes, CancellationToken cancellationToken)
    {
        foreach (var change in changes.Where(change => change.Undoable))
        {
            if (change.Kind == "??")
            {
                if (File.Exists(change.Path)) File.Delete(change.Path);
                continue;
            }

            if (before.IsGit)
            {
                await GitAsync(before.Workspace, ["restore", "--source=HEAD", "--staged", "--worktree", "--", change.Path], cancellationToken);
            }
        }
    }

    /// <summary>Copies the workspace's files back from the copy made before the task and deletes the files it added.</summary>
    public Task RestoreAsync(WorkspaceBefore before, IEnumerable<AgentChange> changes, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (before.Backup is not { } backup) return;
            foreach (var change in changes)
            {
                var relative = Path.GetRelativePath(before.Workspace, change.Path);
                var saved = Path.Combine(backup, relative);
                if (File.Exists(saved))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                    File.Copy(saved, change.Path, overwrite: true);
                }
                else if (File.Exists(change.Path))
                {
                    File.Delete(change.Path);
                }
            }
        }, cancellationToken);

    private string? Backup(string workspace)
    {
        var files = Files(workspace).ToArray();
        if (files.Sum(file => file.Length) > BackupLimit) return null;
        var folder = Path.Combine(_backupsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var file in files)
        {
            var target = Path.Combine(folder, Path.GetRelativePath(workspace, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            file.CopyTo(target);
        }

        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetDirectories(_backupsRoot).OrderByDescending(path => path, StringComparer.Ordinal).Skip(BackupsKept))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A copy that is open somewhere goes with a later task.
            }
        }

        return folder;
    }

    private static IReadOnlyList<AgentChange> CompareWithBackup(WorkspaceBefore before)
    {
        var backup = before.Backup!;
        var changes = new List<AgentChange>();
        var now = Files(before.Workspace).ToDictionary(file => Path.GetRelativePath(before.Workspace, file.FullName), StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, file) in now)
        {
            var saved = new FileInfo(Path.Combine(backup, relative));
            if (!saved.Exists) changes.Add(new AgentChange(file.FullName, "??", true));
            else if (saved.Length != file.Length || !SameContent(saved, file)) changes.Add(new AgentChange(file.FullName, "M", true));
        }

        foreach (var saved in Files(backup))
        {
            var relative = Path.GetRelativePath(backup, saved.FullName);
            if (!now.ContainsKey(relative)) changes.Add(new AgentChange(Path.Combine(before.Workspace, relative), "D", true));
        }

        return changes;
    }

    private static bool SameContent(FileInfo first, FileInfo second)
    {
        // The copy keeps the original's write time: an untouched file is not read at all.
        if (first.LastWriteTimeUtc == second.LastWriteTimeUtc) return true;
        using var a = first.OpenRead();
        using var b = second.OpenRead();
        var bufferA = new byte[81920];
        var bufferB = new byte[81920];
        while (true)
        {
            var read = a.Read(bufferA);
            if (read == 0) return true;
            b.ReadExactly(bufferB, 0, read);
            if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read))) return false;
        }
    }

    private static IEnumerable<FileInfo> Files(string root)
    {
        var folders = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (folders.TryPop(out var folder))
        {
            FileSystemInfo[] entries;
            try
            {
                entries = folder.GetFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo child)
                {
                    if (!SkippedFolders.Contains(child.Name) && !child.Attributes.HasFlag(FileAttributes.ReparsePoint)) folders.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    yield return file;
                }
            }
        }
    }

    private static string Fingerprint(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
    }

    /// <summary>The workspace's uncommitted files as full paths with their status code; null when it is not in git.</summary>
    private static async Task<IReadOnlyList<(string Path, string Code)>?> GitStatusAsync(string workspace, CancellationToken cancellationToken)
    {
        var root = await GitAsync(workspace, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (root is null) return null;
        var top = root.Trim().Replace('/', Path.DirectorySeparatorChar);
        var output = await GitAsync(workspace, ["-c", "core.quotepath=off", "status", "--porcelain", "--untracked-files=all", "--", "."], cancellationToken);
        if (output is null) return null;
        var entries = new List<(string, string)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var code = line[..2].Trim();
            var path = line[3..].TrimEnd('\r');
            // A rename is "old -> new": the new name is what is in the workspace now.
            if (path.Contains(" -> ", StringComparison.Ordinal)) path = path[(path.IndexOf(" -> ", StringComparison.Ordinal) + 4)..];
            path = path.Trim('"');
            entries.Add((Path.GetFullPath(Path.Combine(top, path)), code));
        }

        return entries;
    }

    /// <summary>Runs git in the workspace; its output, or null when git is missing or the command failed.</summary>
    private static async Task<string?> GitAsync(string workspace, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var output = new System.Text.StringBuilder();
        try
        {
            var result = await AgentProcess.RunAsync(
                new AgentCommand("git", arguments, workspace, string.Empty),
                line => output.AppendLine(line),
                null,
                cancellationToken);
            return result.ExitCode == 0 ? output.ToString() : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
