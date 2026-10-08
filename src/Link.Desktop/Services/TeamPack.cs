using System.IO.Compression;
using System.Text.Json;

namespace Link.Desktop.Services;

/// <summary>What an import did, by the item's name as the pack has it: <c>roles/qa.md</c>, <c>skills/game-check</c>.</summary>
public sealed record TeamPackImport(IReadOnlyList<string> Added, IReadOnlyList<string> Replaced, IReadOnlyList<string> Skipped);

/// <summary>
/// A team pack: the workspace's own roles and skills in one zip (<c>.linkpack</c>), to give to a teammate's workspace.
/// It holds <c>roles/*.md</c>, <c>skills/&lt;name&gt;/**</c> and a <c>pack.json</c>, and never anything else: no settings,
/// keys, projects or logs.
/// </summary>
public static class TeamPack
{
    public const string Extension = ".linkpack";

    private const int Format = 1;
    private const string ManifestFile = "pack.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A link (symlink, junction) may lead anywhere: the pack holds what the folders themselves hold.
    private static readonly EnumerationOptions Flat = new() { AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly EnumerationOptions Deep = new() { AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = true };

    // What no part of an entry's path may hold: Windows takes a backslash and a drive colon for a path whatever the zip says.
    private static readonly char[] NotInName = [.. Path.GetInvalidFileNameChars(), '\\', ':'];

    private sealed record Manifest(int Format, string Created, IReadOnlyList<string> Roles, IReadOnlyList<string> Skills);

    /// <summary>
    /// Writes the workspace's roles and skills to <paramref name="file"/> and tells how many of each. A workspace with
    /// neither gets no file.
    /// </summary>
    public static (int Roles, int Skills) Export(string workspace, string file)
    {
        var rolesFolder = ProjectRoles.FolderIn(workspace);
        var skillsFolder = ProjectSkills.FolderIn(workspace);
        var roles = Directory.Exists(rolesFolder) ? Directory.GetFiles(rolesFolder, "*.md", Flat).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        var skills = Directory.Exists(skillsFolder)
            ? Directory.GetDirectories(skillsFolder, "*", Flat)
                .Where(folder => File.Exists(Path.Combine(folder, "SKILL.md")))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        if (roles.Length == 0 && skills.Length == 0) return (0, 0);

        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var role in roles) zip.CreateEntryFromFile(role, $"roles/{Path.GetFileName(role)}", CompressionLevel.Optimal);
        foreach (var path in skills.SelectMany(folder => Directory.GetFiles(folder, "*", Deep)))
        {
            zip.CreateEntryFromFile(path, $"skills/{Path.GetRelativePath(skillsFolder, path).Replace('\\', '/')}", CompressionLevel.Optimal);
        }

        var manifest = new Manifest(
            Format,
            DateTimeOffset.Now.ToString("o"),
            [.. roles.Select(Path.GetFileNameWithoutExtension).OfType<string>()],
            [.. skills.Select(Path.GetFileName).OfType<string>()]);
        using var writer = zip.CreateEntry(ManifestFile).Open();
        JsonSerializer.Serialize(writer, manifest, Json);
        return (roles.Length, skills.Length);
    }

    /// <summary>
    /// Puts the pack's roles and skills into the workspace's <c>.agents</c>. A role or skill the workspace has already is
    /// replaced only when <paramref name="replace"/> says yes to its name (a replaced skill keeps files the pack does
    /// not have). A pack that is not one of this version, or has an entry that leads out of <c>.agents</c> (a <c>..</c>
    /// part, a rooted path), is refused before anything is written.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is no team pack, or it is not safe.</exception>
    public static TeamPackImport Import(string file, string workspace, Func<string, bool> replace)
    {
        using var zip = ZipFile.OpenRead(file);
        if (ReadManifest(zip)?.Format != Format) throw new InvalidDataException("This is not a LINK team pack of this version.");

        var agents = Path.GetFullPath(Path.Combine(workspace, ".agents"));
        var items = new List<(string Item, ZipArchiveEntry Entry, string Target)>();
        foreach (var entry in zip.Entries)
        {
            if (Classify(entry.FullName) is not { } found) continue;
            var target = Path.GetFullPath(Path.Combine(agents, found.Relative));
            if (!target.StartsWith(agents + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The pack has an entry outside .agents: {entry.FullName}");
            }

            items.Add((found.Item, entry, target));
        }

        var (added, replaced, skipped) = (new List<string>(), new List<string>(), new List<string>());
        foreach (var item in items.GroupBy(each => each.Item, StringComparer.OrdinalIgnoreCase))
        {
            var existing = Path.Combine(agents, item.Key);
            var exists = File.Exists(existing) || Directory.Exists(existing);
            if (exists && !replace(item.Key))
            {
                skipped.Add(item.Key);
                continue;
            }

            foreach (var (_, entry, target) in item)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            (exists ? replaced : added).Add(item.Key);
        }

        return new TeamPackImport(added, replaced, skipped);
    }

    private static Manifest? ReadManifest(ZipArchive zip)
    {
        if (zip.GetEntry(ManifestFile) is not { } entry) return null;
        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<Manifest>(stream, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The item an entry is part of (<c>roles/qa.md</c>, <c>skills/game-check</c>) and its path under <c>.agents</c>; null
    /// for a folder, the manifest and whatever else a pack carries, which is not taken. Any entry whose path is rooted or
    /// has a part that goes up, is empty or is no file name throws, taken or not.
    /// </summary>
    private static (string Item, string Relative)? Classify(string name)
    {
        var folder = name.EndsWith('/');
        var parts = (folder ? name[..^1] : name).Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.IndexOfAny(NotInName) >= 0))
        {
            throw new InvalidDataException($"The pack has an entry that is not a safe path: {name}");
        }

        if (folder) return null;
        return parts switch
        {
            ["roles", var role] when role.EndsWith(".md", StringComparison.OrdinalIgnoreCase) => (name, name),
            ["skills", var skill, _, ..] => ($"skills/{skill}", name),
            _ => null
        };
    }
}
