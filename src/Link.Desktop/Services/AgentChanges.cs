namespace Link.Desktop.Services;

/// <summary>A file an agent changed. <see cref="Undoable"/>: it was clean before the task, so undoing loses nothing of the user's.</summary>
public sealed record AgentChange(string Path, string Kind, bool Undoable);

/// <summary>
/// What a workspace looked like before a task: in git, its pending files with a fingerprint each; without it, every
/// file with its fingerprint.
/// </summary>
public sealed record WorkspaceBefore(string Workspace, bool IsGit, IReadOnlyDictionary<string, string> Dirty, DateTime StartedUtc)
{
    /// <summary>In git: the commit a diff of the run is taken from, so that it shows the run's own work; null: HEAD.</summary>
    public string? Snapshot { get; init; }

    /// <summary>Whether a diff shows only what was done since: else earlier changes to the same files show too.</summary>
    public bool DiffsOwnWork => IsGit && Snapshot is not null;
}

/// <summary>
/// Finds what an agent changed and puts it back. In git: status before and after, and undo only files that were clean
/// before. Without git: the files' sizes and times tell what changed, and nothing is undone.
/// Nothing is undone without the user's yes, asked by the caller.
/// </summary>
public sealed class AgentChanges
{
    // Pending files are copied into the repository for a diff of the run only up to these (see SnapshotAsync): an asset
    // would take disk, and so many paths would not fit in a command line.
    private const long SnapshotFileLimit = 1024 * 1024;
    private const int SnapshotFilesMost = 200;
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "__pycache__",
        // Unity and Unreal rebuild these themselves.
        "Library", "Temp", "Intermediate", "Saved", "DerivedDataCache", "Binaries"
    };

    // Folders of a Unity project (Assets beside ProjectSettings) that its editor writes by itself.
    private static readonly HashSet<string> UnityWrites = new(StringComparer.OrdinalIgnoreCase) { "Logs", "UserSettings" };

    public async Task<WorkspaceBefore> CaptureAsync(string workspace, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var status = await GitStatusAsync(workspace, cancellationToken);
        if (status is not null)
        {
            var dirty = status.ToDictionary(entry => entry.Path, entry => Fingerprint(entry.Path), StringComparer.OrdinalIgnoreCase);
            return new WorkspaceBefore(workspace, true, dirty, started) { Snapshot = await SnapshotAsync(workspace, status, cancellationToken) };
        }

        return new WorkspaceBefore(workspace, false, await Task.Run(() => Prints(workspace), cancellationToken), started);
    }

    /// <summary>
    /// The files changed since <paramref name="before"/>. Without git: by size and time, and none can be undone.
    /// </summary>
    public async Task<IReadOnlyList<AgentChange>> CompareAsync(WorkspaceBefore before, CancellationToken cancellationToken)
    {
        if (before.IsGit)
        {
            var after = await GitStatusAsync(before.Workspace, cancellationToken) ?? [];
            return ChangedSince(before, after);
        }

        return await Task.Run(() => CompareWithPrints(before), cancellationToken);
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

    private static IReadOnlyList<AgentChange> CompareWithPrints(WorkspaceBefore before)
    {
        var now = Prints(before.Workspace);
        var changes = now
            .Where(entry => !before.Dirty.TryGetValue(entry.Key, out var print) || print != entry.Value)
            .Select(entry => new AgentChange(entry.Key, before.Dirty.ContainsKey(entry.Key) ? "M" : "??", false))
            .ToList();
        changes.AddRange(before.Dirty.Keys.Where(path => !now.ContainsKey(path)).Select(path => new AgentChange(path, "D", false)));
        return changes;
    }

    /// <summary>Each file of the workspace, as <see cref="Files"/> walks it, with its size and write time.</summary>
    private static Dictionary<string, string> Prints(string workspace) =>
        Files(workspace).ToDictionary(file => file.FullName, file => $"{file.Length}:{file.LastWriteTimeUtc.Ticks}", StringComparer.OrdinalIgnoreCase);

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

            // An open Unity editor rewrites its project's logs, user settings and IDE projects all the time: they are not
            // the agent's work (a task once showed 787 "changed" files of them).
            var unity = entries.Any(entry => entry is DirectoryInfo { Name: "Assets" }) && entries.Any(entry => entry is DirectoryInfo { Name: "ProjectSettings" });
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo child)
                {
                    if (!SkippedFolders.Contains(child.Name) && !(unity && UnityWrites.Contains(child.Name)) &&
                        !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        folders.Push(child);
                    }
                }
                else if (entry is FileInfo file && !(unity && file.Extension is ".csproj" or ".sln"))
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

    /// <summary>
    /// The tree a diff of the coming run is taken from: HEAD when no tracked file of the workspace is pending, else HEAD
    /// with those files as they are now, which git writes into its objects through an index of Link's own (the user's
    /// index and files stay as they are). Null, and the diff is against HEAD, where a pending file is big or git cannot.
    /// </summary>
    private static async Task<string?> SnapshotAsync(string workspace, IReadOnlyList<(string Path, string Code)> status, CancellationToken cancellationToken)
    {
        var tracked = status.Where(entry => entry.Code != "??").Select(entry => entry.Path).ToArray();
        if (tracked.Length == 0) return "HEAD";
        if (tracked.Length > SnapshotFilesMost || tracked.Any(path => File.Exists(path) && new FileInfo(path).Length > SnapshotFileLimit)) return null;
        var index = Path.Combine(Path.GetTempPath(), $"link-index-{Guid.NewGuid():N}");
        var own = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            // Only the pending files are read: an index without git's notes of the others would have it read them all.
            if (await GitAsync(workspace, ["read-tree", "HEAD"], cancellationToken, own) is null ||
                await GitAsync(workspace, ["add", "--", .. tracked.Select(path => Path.GetRelativePath(workspace, path))], cancellationToken, own) is null)
            {
                return null;
            }

            return (await GitAsync(workspace, ["write-tree"], cancellationToken, own))?.Trim() is { Length: > 0 } tree ? tree : null;
        }
        finally
        {
            try
            {
                File.Delete(index);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file left in the temporary folder harms nothing.
            }
        }
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
    /// What the agent changed in a git workspace, since <paramref name="before"/> where its snapshot tells (else against the
    /// last commit): lines added and removed per file (<paramref name="stat"/>), or the diff itself cut at
    /// <paramref name="limit"/> characters. Null without git or changes; files git does not track yet are only listed.
    /// </summary>
    public async Task<string?> DiffAsync(WorkspaceBefore before, IEnumerable<AgentChange> changes, bool stat, int limit,
        CancellationToken cancellationToken)
    {
        if (!before.IsGit) return null;
        var paths = changes.Select(change => Path.GetRelativePath(before.Workspace, change.Path)).ToArray();
        if (paths.Length == 0) return null;
        var from = before.Snapshot ?? "HEAD";
        string[] arguments = stat ? ["diff", "--no-color", "--numstat", from, "--", .. paths] : ["diff", "--no-color", from, "--", .. paths];
        var diff = (await GitAsync(before.Workspace, arguments, cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(diff)) return null;
        return diff.Length <= limit ? diff : diff[..limit] + $"\n… cut at {limit / 1000} KB of {diff.Length / 1000} KB";
    }

    /// <summary>
    /// The lines added and removed in <paramref name="files"/> (relative to the workspace) against the last commit, as git
    /// counts them; null without git or changes. For a task that is opened later, when what the workspace looked like
    /// before it is no longer at hand.
    /// </summary>
    public async Task<string?> NumstatAsync(string workspace, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0) return null;
        var numstat = await GitAsync(workspace, ["diff", "--no-color", "--numstat", "HEAD", "--", .. files], cancellationToken);
        return string.IsNullOrWhiteSpace(numstat) ? null : numstat;
    }

    /// <summary>
    /// The files a task changed so far, as they are when its next run starts: with them <see cref="Own"/> tells that
    /// run's own changes from the task's.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Marks(IEnumerable<AgentChange> changed) =>
        changed.DistinctBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(change => change.Path, change => Fingerprint(change.Path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Of a task's changes after a run, those the run made itself: a file new to the task, or changed again.</summary>
    public static IReadOnlyList<AgentChange> Own(IReadOnlyList<AgentChange> changes, IReadOnlyDictionary<string, string> marks) =>
        [.. changes.Where(change => !marks.TryGetValue(change.Path, out var mark) || mark != Fingerprint(change.Path))];

    private static IReadOnlyList<AgentChange> ChangedSince(WorkspaceBefore before,
        IReadOnlyList<(string Path, string Code)> after) =>
        after
            .Where(entry => !before.Dirty.TryGetValue(entry.Path, out var print) || print != Fingerprint(entry.Path))
            .Select(entry => new AgentChange(entry.Path, entry.Code, !before.Dirty.ContainsKey(entry.Path)))
            .ToArray();

    /// <summary>Whether the folder is in a git repository: a .git in it or above.</summary>
    public static bool IsGit(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) return true;
        }

        return false;
    }

    /// <summary>The repositories right inside a folder that is in none, by their folder names: where its git lives.</summary>
    public static IReadOnlyList<string> RepositoriesInside(string folder)
    {
        if (IsGit(folder) || !Directory.Exists(folder)) return [];
        try
        {
            return [.. Directory.EnumerateDirectories(folder)
                .Where(child => Directory.Exists(Path.Combine(child, ".git")) || File.Exists(Path.Combine(child, ".git")))
                .Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether the folder is in a Perforce workspace: a .p4config in it or above, as P4V and p4 find it. The agent is
    /// told how to open files there; Link lists its changes as in a folder without git.
    /// </summary>
    public static bool IsPerforce(string folder)
    {
        for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".p4config"))) return true;
        }

        return false;
    }

    /// <summary>
    /// The file as the last commit has it, to show beside the file as it is now (a picture before and after); null outside
    /// git, for a file git does not track, or when it has not changed. Bytes as they are: git's line endings are not undone.
    /// </summary>
    public static async Task<byte[]?> CommittedAsync(string file, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(file);
        if (folder is null || !File.Exists(file) || !IsGit(folder)) return null;
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-C", folder, "show", "HEAD:./" + Path.GetFileName(file) }) start.ArgumentList.Add(argument);
        try
        {
            using var git = System.Diagnostics.Process.Start(start);
            if (git is null) return null;
            using var output = new MemoryStream();
            var errors = git.StandardError.ReadToEndAsync(cancellationToken);
            await git.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            await git.WaitForExitAsync(cancellationToken);
            await errors;
            if (git.ExitCode != 0) return null;
            var committed = output.ToArray();
            var now = await File.ReadAllBytesAsync(file, cancellationToken);
            return committed.AsSpan().SequenceEqual(now) ? null : committed;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Runs git in the workspace; its output, or null when git is missing or the command failed.</summary>
    private static async Task<string?> GitAsync(string workspace, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var output = new System.Text.StringBuilder();
        try
        {
            var result = await AgentProcess.RunAsync(
                new AgentCommand("git", arguments, workspace, string.Empty) { EnvironmentVariables = environment ?? new Dictionary<string, string>() },
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
