using System.Globalization;

namespace Link.Desktop.Services;

/// <summary>A file an agent changed. <see cref="Undoable"/>: it was clean before the task, so undoing loses nothing of the user's.</summary>
public sealed record AgentChange(string Path, string Kind, bool Undoable);

/// <summary>
/// What a workspace looked like before a task: in git or Perforce, its pending files with a fingerprint each;
/// without version control, the copy made before Do, if any.
/// </summary>
public sealed record WorkspaceBefore(string Workspace, bool IsGit, IReadOnlyDictionary<string, string> Dirty, string? Backup,
    DateTime StartedUtc, bool IsPerforce = false)
{
    public bool IsVersioned => IsGit || IsPerforce;
}

/// <summary>
/// Finds what an agent changed and puts it back. In git or Perforce: status before and after, and undo only files that
/// were clean before. Without version control: a copy of the workspace before Do, compared and restored file by file.
/// Nothing is undone or restored without the user's yes, asked by the caller.
/// </summary>
public sealed partial class AgentChanges
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

        if (IsPerforce(workspace))
        {
            var perforce = await P4StatusAsync(workspace, cancellationToken)
                           ?? throw new IOException("Perforce is configured but unavailable. Sign in with P4V or run p4 login.");
            var dirty = perforce.ToDictionary(entry => entry.Path, entry => Fingerprint(entry.Path), StringComparer.OrdinalIgnoreCase);
            return new WorkspaceBefore(workspace, false, dirty, null, started, IsPerforce: true);
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
            return ChangedSince(before, after);
        }

        if (before.IsPerforce)
        {
            var after = await P4StatusAsync(before.Workspace, cancellationToken)
                        ?? throw new IOException("Perforce became unavailable. Sign in with P4V or run p4 login.");
            return ChangedSince(before, after);
        }

        return before.Backup is null ? [] : await Task.Run(() => CompareWithBackup(before), cancellationToken);
    }

    /// <summary>Puts back the undoable changes: a new file is deleted, a changed or deleted one comes back from HEAD.</summary>
    public async Task UndoAsync(WorkspaceBefore before, IEnumerable<AgentChange> changes, CancellationToken cancellationToken)
    {
        if (before.IsPerforce)
        {
            foreach (var change in changes.Where(change => change.Undoable))
            {
                await P4UndoAsync(before.Workspace, change, cancellationToken);
            }

            return;
        }

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

    /// <summary>
    /// What the agent changed in a git workspace, against the last commit: lines added and removed per file
    /// (<paramref name="stat"/>), or the diff itself cut at <paramref name="limit"/> characters. Null without git or
    /// changes; files git does not track yet are only listed.
    /// </summary>
    public async Task<string?> DiffAsync(WorkspaceBefore before, IEnumerable<AgentChange> changes, bool stat, int limit,
        CancellationToken cancellationToken)
    {
        if (before.IsPerforce) return await P4DiffAsync(before, changes, stat, limit, cancellationToken);
        if (!before.IsGit) return null;
        var paths = changes.Select(change => Path.GetRelativePath(before.Workspace, change.Path)).ToArray();
        if (paths.Length == 0) return null;
        string[] arguments = stat ? ["diff", "--no-color", "--numstat", "HEAD", "--", .. paths] : ["diff", "--no-color", "HEAD", "--", .. paths];
        var diff = (await GitAsync(before.Workspace, arguments, cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(diff)) return null;
        return diff.Length <= limit ? diff : diff[..limit] + $"\n… cut at {limit / 1000} KB of {diff.Length / 1000} KB";
    }

    private static IReadOnlyList<AgentChange> ChangedSince(WorkspaceBefore before,
        IReadOnlyList<(string Path, string Code)> after) =>
        after
            .Where(entry => !before.Dirty.TryGetValue(entry.Path, out var print) || print != Fingerprint(entry.Path))
            .Select(entry => new AgentChange(entry.Path, entry.Code, !before.Dirty.ContainsKey(entry.Path)))
            .ToArray();

    private static async Task<IReadOnlyList<(string Path, string Code)>?> P4StatusAsync(string workspace,
        CancellationToken cancellationToken)
    {
        var info = await P4Async(workspace, ["-ztag", "info"], cancellationToken);
        if (info is not { ExitCode: 0 } || !info.Output.Contains("... clientName ", StringComparison.Ordinal) ||
            info.Output.Contains("... clientName *unknown*", StringComparison.Ordinal)) return null;

        // fstat names opened files by their local path (opened would give the client's syntax); reconcile previews
        // what changed without being opened, judged by the files' times first (-m): a full digest pass takes seconds.
        var opened = await P4Async(workspace, ["-ztag", "fstat", "-Ro", "-T", "clientFile,action", "..."], cancellationToken);
        var reconcile = await P4Async(workspace, ["-ztag", "reconcile", "-n", "-e", "-a", "-d", "-m", "..."], cancellationToken);
        return ParseP4Status(opened?.Output ?? string.Empty, reconcile?.Output ?? string.Empty);
    }

    /// <summary>
    /// In Perforce: the files the agent changed, those that were clean before the task, go into the task's pending
    /// changelist, made the first time with <paramref name="description"/>, so P4V shows the task's work together and
    /// the user submits it there. A file the agent changed without opening it is opened there too. Returns the
    /// changelist; null when Perforce made none.
    /// </summary>
    public async Task<int?> KeepInChangelistAsync(WorkspaceBefore before, IEnumerable<AgentChange> changes, string description,
        int? changelist, CancellationToken cancellationToken)
    {
        var files = changes.Where(change => change.Undoable).Select(change => P4File(change.Path)).ToArray();
        if (!before.IsPerforce || files.Length == 0) return changelist;
        var number = changelist ?? await NewChangelistAsync(before.Workspace, description, cancellationToken);
        if (number is not { } change) return null;
        var target = change.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var opened = await P4Async(before.Workspace, ["-ztag", "fstat", "-Ro", "-T", "clientFile", .. files], cancellationToken);
        var open = ParseP4Paths(opened?.Output ?? string.Empty);
        var reopen = files.Where(file => open.Contains(Path.GetFullPath(P4Path(file)))).ToArray();
        var reconcile = files.Except(reopen).ToArray();
        if (reopen.Length > 0) await P4Async(before.Workspace, ["reopen", "-c", target, .. reopen], cancellationToken);
        if (reconcile.Length > 0) await P4Async(before.Workspace, ["reconcile", "-c", target, "-e", "-a", "-d", .. reconcile], cancellationToken);
        return change;
    }

    /// <summary>A task's changelist with no files left, after an undo, is deleted; one that still holds files stays.</summary>
    public static async Task DropChangelistIfEmptyAsync(string workspace, int changelist, CancellationToken cancellationToken)
    {
        // Perforce refuses to delete a changelist that holds files, which is what is wanted here.
        await P4Async(workspace, ["change", "-d", changelist.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken);
    }

    private static async Task<int?> NewChangelistAsync(string workspace, string description, CancellationToken cancellationToken)
    {
        // A new change's form lists the default changelist's files: emptied, the user's own files stay where they are.
        var form = await P4Async(workspace, ["--field", $"Description={description}", "--field", "Files=", "change", "-o"], cancellationToken);
        if (form is not { ExitCode: 0 }) return null;
        var created = await P4Async(workspace, ["change", "-i"], cancellationToken, form.Output);
        return created is { ExitCode: 0 } && ChangeCreated().Match(created.Output) is { Success: true } match
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static HashSet<string> ParseP4Paths(string output) => output.Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.StartsWith("... clientFile ", StringComparison.Ordinal))
        .Select(line => Path.GetFullPath(line[15..]))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // P4File's escapes, undone: the path as the disk names it.
    private static string P4Path(string file) => file
        .Replace("%2A", "*", StringComparison.Ordinal)
        .Replace("%40", "@", StringComparison.Ordinal)
        .Replace("%23", "#", StringComparison.Ordinal)
        .Replace("%25", "%", StringComparison.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(@"Change (\d+) created")]
    private static partial System.Text.RegularExpressions.Regex ChangeCreated();

    internal static IReadOnlyList<(string Path, string Code)> ParseP4Status(params string[] outputs)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            string? path = null;
            string? action = null;
            void Add()
            {
                if (path is not null && action is not null) entries[Path.GetFullPath(path)] = P4Code(action);
                path = null;
                action = null;
            }

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("... clientFile ", StringComparison.Ordinal))
                {
                    Add();
                    path = line[15..];
                }
                else if (line.StartsWith("... action ", StringComparison.Ordinal))
                {
                    action = line[11..];
                }
                else if (line.Length == 0)
                {
                    Add();
                }
            }

            Add();
        }

        return entries.Select(entry => (entry.Key, entry.Value)).ToArray();
    }

    private static string P4Code(string action) => action switch
    {
        "add" or "move/add" or "branch" => "??",
        "delete" or "move/delete" => "D",
        _ => "M"
    };

    private static async Task P4UndoAsync(string workspace, AgentChange change, CancellationToken cancellationToken)
    {
        var file = P4File(change.Path);
        var revert = await P4Async(workspace, ["revert", file], cancellationToken)
                     ?? throw new IOException("Could not start p4 revert.");
        if (revert.ExitCode != 0 && !revert.Output.Contains("not opened", StringComparison.OrdinalIgnoreCase) &&
            !revert.Error.Contains("not opened", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(ShortError("p4 revert", revert));
        }

        if (change.Kind == "??")
        {
            if (File.Exists(change.Path)) File.Delete(change.Path);
            return;
        }

        var sync = await P4Async(workspace, ["sync", "-f", file + "#have"], cancellationToken);
        if (sync is not { ExitCode: 0 }) throw new IOException(ShortError("p4 sync", sync));
    }

    private static async Task<string?> P4DiffAsync(WorkspaceBefore before, IEnumerable<AgentChange> changes, bool stat,
        int limit, CancellationToken cancellationToken)
    {
        var files = changes.ToArray();
        if (files.Length == 0) return null;
        if (stat)
        {
            return string.Join('\n', files.Select(change =>
                $"{change.Kind}\t{Path.GetRelativePath(before.Workspace, change.Path)}"));
        }

        var text = new System.Text.StringBuilder();
        foreach (var change in files)
        {
            var relative = Path.GetRelativePath(before.Workspace, change.Path);
            if (change.Kind == "??")
            {
                text.Append("?? ").AppendLine(relative);
                continue;
            }

            var diff = await P4Async(before.Workspace, ["diff", "-du", "-f", P4File(change.Path)], cancellationToken);
            if (!string.IsNullOrWhiteSpace(diff?.Output)) text.AppendLine(diff.Output.TrimEnd());
            else text.Append(change.Kind).Append(' ').Append(relative).AppendLine(" (binary or metadata change)");
            if (text.Length > limit) break;
        }

        var result = text.ToString().Trim();
        if (result.Length == 0) return null;
        return result.Length <= limit ? result : result[..limit] + $"\n… cut at {limit / 1000} KB of {result.Length / 1000} KB";
    }

    /// <summary>Whether the folder is in a git repository: a .git in it or above.</summary>
    public static bool IsGit(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) return true;
        }

        return false;
    }

    /// <summary>Whether the folder is in a Perforce workspace: a .p4config in it or above, as P4V and p4 find it.</summary>
    public static bool IsPerforce(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".p4config"))) return true;
        }

        return false;
    }

    private static string P4File(string path) => path
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("#", "%23", StringComparison.Ordinal)
        .Replace("@", "%40", StringComparison.Ordinal)
        .Replace("*", "%2A", StringComparison.Ordinal);

    private static string P4Executable()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Perforce", "p4.exe");
        return File.Exists(installed) ? installed : "p4";
    }

    private sealed record CommandOutput(int ExitCode, string Output, string Error);

    private static async Task<CommandOutput?> P4Async(string workspace, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, string input = "")
    {
        var output = new System.Text.StringBuilder();
        var errors = new System.Text.StringBuilder();
        try
        {
            var command = new AgentCommand(P4Executable(), arguments, workspace, input)
            {
                EnvironmentVariables = new Dictionary<string, string> { ["P4CONFIG"] = ".p4config" }
            };
            var result = await AgentProcess.RunAsync(command, line => output.AppendLine(line), line => errors.AppendLine(line),
                cancellationToken);
            return new CommandOutput(result.ExitCode, output.ToString(), errors.ToString());
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ShortError(string operation, CommandOutput? result)
    {
        if (result is null) return $"Could not start {operation}.";
        var text = (result.Error + result.Output).Trim();
        return text.Length == 0 ? $"{operation} failed with exit code {result.ExitCode}." : text.Split('\n')[^1].Trim();
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
