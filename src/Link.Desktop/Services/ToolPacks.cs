using System.Security.Cryptography;
using System.Text.Json;

namespace Link.Desktop.Services;

/// <summary>A tool pack: what Link knows of an engine or a tool (Unreal, Unity, Blender…) for every project that uses it.</summary>
public sealed record ToolPack(string Id, string Name, string Description, IReadOnlyList<string> Detect, string Folder)
{
    /// <summary>Packs that bring this one along: a tool used in every game of these engines (Blender with Unity and Unreal).</summary>
    public IReadOnlyList<string> With { get; init; } = [];
}

/// <summary>
/// Tool packs: one folder per engine or tool in the packs folder, shared by all projects. A pack holds <c>pack.md</c>
/// (name, description, and <c>detect:</c> the files a project of it has), <c>skills/&lt;name&gt;/SKILL.md</c> and
/// <c>roles/&lt;role&gt;.md</c>, what a specialist does in this engine or tool, added to the role's own text. A project
/// takes the packs its folder shows; its own skills and roles override theirs. Link's own packs ship beside Link.exe
/// and are copied into the folder where it lacks them, so that the user's changes stay.
/// </summary>
public static class ToolPacks
{
    /// <summary>How long what a folder showed is kept: the card and the inspector ask often, and a folder rarely changes its engine.</summary>
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(1);

    // An engine's marker lies at the top of the project or a level or two below it (a .blend in Art/Characters).
    private static readonly EnumerationOptions Near = new()
    {
        RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
    };

    // The folders at the top, without hidden ones (.git) and links.
    private static readonly EnumerationOptions Top = new()
    {
        IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System
    };

    private static readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<ToolPack> Packs)> Seen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>The packs folder of Settings; Link sets it at startup and when Settings change it.</summary>
    public static string Folder { get; set; } = DefaultFolder;

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LinkPacks");

    public static string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, "harness", "packs");

    /// <summary>The packs of the folder by id; a folder without a readable pack.md with a name is not a pack.</summary>
    public static IReadOnlyList<ToolPack> All(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var packs = new List<ToolPack>();
        try
        {
            foreach (var directory in Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(directory, "pack.md");
                if (!File.Exists(file)) continue;
                var (fields, _) = ProjectSkills.FrontMatter(AgentRules.WithoutNotes(File.ReadAllText(file)));
                if (!fields.TryGetValue("name", out var name)) continue;
                packs.Add(new ToolPack(Path.GetFileName(directory), name, fields.GetValueOrDefault("description") ?? string.Empty,
                    List(fields.GetValueOrDefault("detect")), directory) { With = List(fields.GetValueOrDefault("with")) });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What was read so far is still true.
        }

        return packs;
    }

    /// <summary>
    /// The packs a workspace uses: those its files show (see <see cref="Shows"/>), and those that come <see cref="ToolPack.With"/>
    /// one of them; none for no workspace.
    /// </summary>
    public static IReadOnlyList<ToolPack> For(string? workspace)
    {
        if (workspace is null || !Directory.Exists(workspace)) return [];
        lock (Gate)
        {
            if (Seen.TryGetValue(workspace, out var seen) && DateTimeOffset.Now - seen.At < Kept) return seen.Packs;
        }

        var all = All(Folder);
        var shown = all.Where(pack => pack.Detect.Any(marker => Shows(workspace, marker))).Select(pack => pack.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ToolPack> packs = [.. all.Where(pack => shown.Contains(pack.Id) || pack.With.Any(shown.Contains))];
        lock (Gate) Seen[workspace] = (DateTimeOffset.Now, packs);
        return packs;
    }

    /// <summary>
    /// Whether a workspace has a pack's marker: a name with a star is a file pattern at the top or two levels below
    /// (<c>*.uproject</c>, <c>*.blend</c>); any other name or path is a file or folder at that place, at the top or in a
    /// folder of the top (<c>ProjectSettings/ProjectVersion.txt</c>, <c>project.godot</c>, <c>.p4config</c>).
    /// </summary>
    public static bool Shows(string workspace, string marker)
    {
        try
        {
            if (marker.Contains('/') || !marker.Contains('*'))
            {
                // A repository often keeps the engine's project in a folder of its own, beside Art and docs.
                var relative = marker.Replace('/', Path.DirectorySeparatorChar);
                return Directory.EnumerateDirectories(workspace, "*", Top).Prepend(workspace)
                    .Select(folder => Path.Combine(folder, relative))
                    .Any(path => File.Exists(path) || Directory.Exists(path));
            }

            return Directory.EnumerateFiles(workspace, marker, Near).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string[] List(string? value) => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    /// <summary>The packs a workspace uses, by name: "Unreal + Blender + Perforce"; empty for none.</summary>
    public static string Names(string? workspace) => string.Join(" + ", For(workspace).Select(pack => pack.Name));

    /// <summary>
    /// The workspace's own copies of its packs' skills that differ from the pack's and were written since
    /// <paramref name="since"/>: a lesson an agent kept for this project, which the user may give to all of them.
    /// </summary>
    public static IReadOnlyList<(ProjectSkill Copy, ProjectSkill Shared)> Lessons(string workspace, DateTimeOffset since)
    {
        var own = ProjectSkills.Own(workspace);
        var lessons = new List<(ProjectSkill, ProjectSkill)>();
        foreach (var shared in For(workspace).SelectMany(ProjectSkills.Of))
        {
            if (own.FirstOrDefault(skill => string.Equals(skill.Name, shared.Name, StringComparison.OrdinalIgnoreCase)) is not { } copy) continue;
            try
            {
                var folder = Path.GetDirectoryName(copy.FilePath)!;
                var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                if (files.Max(File.GetLastWriteTimeUtc) >= since.UtcDateTime && Differs(folder, Path.GetDirectoryName(shared.FilePath)!, files)) lessons.Add((copy, shared));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A copy that cannot be read is no lesson to share.
            }
        }

        return lessons;
    }

    /// <summary>
    /// Gives a project's copy of a pack's skill to every project: its files go into the pack, and the copy leaves the
    /// project, which reads the pack's again. A copy that cannot be removed (a Perforce file not opened) stays, the same.
    /// </summary>
    public static void Share(ProjectSkill copy, ProjectSkill shared)
    {
        var from = Path.GetDirectoryName(copy.FilePath)!;
        var to = Path.GetDirectoryName(shared.FilePath)!;
        var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        // A read-only file would stop the removal half way: the copy stays whole instead.
        if (files.Any(file => File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly))) return;
        try
        {
            Directory.Delete(from, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The copy is now the same as the pack's: it overrides it with the same text.
        }
    }

    private static bool Differs(string copy, string shared, string[] files) =>
        files.Any(file => Path.Combine(shared, Path.GetRelativePath(copy, file)) is var other &&
                          (!File.Exists(other) || File.ReadAllText(other) != File.ReadAllText(file)));

    /// <summary>
    /// Copies Link's own packs into the packs folder: a file the folder lacks, and a newer Link's version of one that is
    /// still as Link put it there (<see cref="SeededFile"/> keeps a hash of each); a file the user or an agent's shared
    /// lesson changed stays theirs. Returns how many files were copied.
    /// </summary>
    public static int Seed(string from, string to)
    {
        if (!Directory.Exists(from)) return 0;
        var manifest = Path.Combine(to, SeededFile);
        var seeded = AtomicJsonFile.Read<Dictionary<string, string>>(manifest) ?? [];
        var known = seeded.Count;
        var copied = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(from, file).Replace('\\', '/');
                var target = Path.Combine(to, relative);
                var ours = Hash(file);
                if (File.Exists(target))
                {
                    var theirs = Hash(target);
                    if (theirs == ours) seeded[relative] = ours;
                    // A file Link did not put there, or one changed since: the user's.
                    if (theirs == ours || !seeded.TryGetValue(relative, out var put) || put != theirs) continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                    File.Copy(file, target, overwrite: true);
                    seeded[relative] = ours;
                    copied++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A file open elsewhere comes with the next start.
                }
            }

            if (copied > 0 || seeded.Count != known) AtomicJsonFile.Write(manifest, seeded, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next start tries again.
        }

        Forget();
        return copied;
    }

    /// <summary>The packs folder changed (a pack came in): what each workspace showed is read again.</summary>
    public static void Forget()
    {
        lock (Gate) Seen.Clear();
    }

    /// <summary>What Link put into the packs folder, file by file, as a hash: a file still the same is Link's to update.</summary>
    public const string SeededFile = ".link-seeded.json";

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
}
