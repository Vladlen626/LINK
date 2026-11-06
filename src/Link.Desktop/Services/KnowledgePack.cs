using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>Where a knowledge pack's item stands against a packs folder.</summary>
public enum KnowledgeState
{
    /// <summary>Link ships no such skill or role (export), or the packs folder lacks it (import).</summary>
    Added,

    /// <summary>The user's agents changed Link's own (export).</summary>
    Changed,

    /// <summary>The packs folder has it as Link put it there: the pack's replaces it (import).</summary>
    Updates,

    /// <summary>The packs folder has it the same.</summary>
    Same,

    /// <summary>Its owner changed it too: theirs stays, and the pack's goes beside it (import).</summary>
    BothChanged
}

/// <summary>
/// One item of a knowledge pack: a tool pack's skill (<c>blender/skills/blender-model</c>) or role
/// (<c>unity/roles/qa.md</c>), with its description and the lines of it that look personal.
/// </summary>
public sealed record KnowledgeItem(string Item, string Description, KnowledgeState State, IReadOnlyList<string> Personal);

/// <summary>What an import did: the items written into the packs, and those put into <see cref="Beside"/> instead.</summary>
public sealed record KnowledgeImport(IReadOnlyList<string> Taken, IReadOnlyList<string> PutBeside, string Beside);

/// <summary>
/// A knowledge pack: what the user's agents learned in the tool packs, the skills and roles that differ from Link's own,
/// in one <c>.linkpack</c> for a friend's Link, whatever their projects. A skill keeps the Agent Skills layout (a folder
/// with SKILL.md), so the file serves an agent without Link too; its README says how. It holds nothing else: no
/// project, settings, keys or logs.
/// </summary>
public static partial class KnowledgePack
{
    public const string Kind = "learned";

    /// <summary>The folder in the packs folder where an import puts the pack's version of what the user changed too.</summary>
    public const string IncomingFolder = ".incoming";

    private const int Format = 1;
    private const string ManifestFile = "pack.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden };
    private static readonly char[] NotInName = [.. Path.GetInvalidFileNameChars(), '\\', ':'];
    private static readonly HashSet<string> TextFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".py", ".ps1", ".sh", ".js", ".mjs", ".json", ".yaml", ".yml", ".toml", ".cs", ".html", ".css", ".ini", ".cfg"
    };

    private sealed record Manifest(string? Kind, int Format, string Created, IReadOnlyList<string> Items);

    /// <summary>
    /// The skills and roles of the packs folder that Link does not ship as they are: <see cref="KnowledgeState.Added"/>
    /// or <see cref="KnowledgeState.Changed"/>, each with the lines that hold a user folder, an email, a key or one of
    /// <paramref name="personalWords"/> (the user's name, the projects').
    /// </summary>
    public static IReadOnlyList<KnowledgeItem> Learned(string packs, string builtIn, IReadOnlyCollection<string> personalWords)
    {
        var items = new List<KnowledgeItem>();
        foreach (var pack in ToolPacks.All(packs))
        {
            foreach (var item in ItemsOf(pack))
            {
                var path = Local(packs, item);
                var own = Local(builtIn, item);
                KnowledgeState? state = !File.Exists(own) && !Directory.Exists(own) ? KnowledgeState.Added
                    : Files(packs, item).Any(file => Differs(file.Path, Local(builtIn, file.Name))) ? KnowledgeState.Changed
                    : null;
                if (state is { } learned) items.Add(new KnowledgeItem(item, Description(DescriptionFile(path)), learned, Personal(packs, item, personalWords)));
            }
        }

        return items;
    }

    /// <summary>Writes the items of the packs folder to <paramref name="file"/>, with each one's pack.md and a README.</summary>
    public static void Export(string packs, IReadOnlyList<KnowledgeItem> items, string file)
    {
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var pack in items.Select(item => item.Item.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var packFile = Path.Combine(packs, pack, "pack.md");
            if (File.Exists(packFile)) zip.CreateEntryFromFile(packFile, $"packs/{pack}/pack.md", CompressionLevel.Optimal);
        }

        foreach (var (path, name) in items.SelectMany(item => Files(packs, item.Item)))
        {
            zip.CreateEntryFromFile(path, $"packs/{name}", CompressionLevel.Optimal);
        }

        using (var writer = new StreamWriter(zip.CreateEntry("README.md").Open(), new UTF8Encoding(false)))
        {
            writer.Write(Readme(items));
        }

        using var manifest = zip.CreateEntry(ManifestFile).Open();
        JsonSerializer.Serialize(manifest, new Manifest(Kind, Format, DateTimeOffset.Now.ToString("o"), [.. items.Select(item => item.Item)]), Json);
    }

    /// <summary>Whether a file is a knowledge pack (not a team pack); false for one that cannot be read as either.</summary>
    public static bool IsOne(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            return ReadManifest(zip) is { Kind: Kind };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// What the pack would change in the packs folder, item by item. A file of the folder that is still as Link put it
    /// (its hash in <see cref="ToolPacks.SeededFile"/>) is the pack's to replace; one its owner changed is not.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is no knowledge pack of this version, or an entry is not safe.</exception>
    public static IReadOnlyList<KnowledgeItem> Preview(string file, string packs)
    {
        using var zip = ZipFile.OpenRead(file);
        var seeded = AtomicJsonFile.Read<Dictionary<string, string>>(Path.Combine(packs, ToolPacks.SeededFile)) ?? [];
        return [.. Entries(zip).Where(entry => entry.Item is not null).GroupBy(entry => entry.Item!, StringComparer.OrdinalIgnoreCase).Select(item =>
        {
            // A role is its own file; a skill says what it is in its SKILL.md.
            var described = item.FirstOrDefault(entry => string.Equals(entry.Name, item.Key, StringComparison.OrdinalIgnoreCase) ||
                                                         string.Equals(entry.Name, item.Key + "/SKILL.md", StringComparison.OrdinalIgnoreCase));
            return new KnowledgeItem(item.Key, described.Entry is null ? string.Empty : Description(ReadText(described.Entry)), StateOf(item.Key, item, packs, seeded), []);
        })];
    }

    /// <summary>
    /// Takes the items named in <paramref name="take"/> into the packs folder: added and updated ones into their pack (a
    /// pack the folder lacks comes with its pack.md); one its owner changed too goes beside, into
    /// <c>.incoming/&lt;the file's name&gt;</c>, for a merge. One already the same is left.
    /// </summary>
    public static KnowledgeImport Import(string file, string packs, IReadOnlyCollection<string> take)
    {
        var states = Preview(file, packs).ToDictionary(item => item.Item, item => item.State, StringComparer.OrdinalIgnoreCase);
        var beside = Path.Combine(packs, IncomingFolder, Path.GetFileNameWithoutExtension(file));
        var (taken, putBeside) = (new List<string>(), new List<string>());
        using var zip = ZipFile.OpenRead(file);
        var entries = Entries(zip);
        foreach (var item in entries.Where(entry => entry.Item is not null).GroupBy(entry => entry.Item!, StringComparer.OrdinalIgnoreCase))
        {
            if (!take.Contains(item.Key, StringComparer.OrdinalIgnoreCase) || states[item.Key] == KnowledgeState.Same) continue;
            var both = states[item.Key] == KnowledgeState.BothChanged;
            foreach (var entry in item) Extract(entry.Entry, both ? beside : packs, entry.Name);
            (both ? putBeside : taken).Add(item.Key);
        }

        // A pack the folder did not have needs its pack.md to be one.
        foreach (var entry in entries.Where(entry => entry.Item is null))
        {
            var pack = entry.Name.Split('/')[0];
            if (taken.Any(item => item.StartsWith(pack + "/", StringComparison.OrdinalIgnoreCase)) && !File.Exists(Local(packs, entry.Name))) Extract(entry.Entry, packs, entry.Name);
        }

        ToolPacks.Forget();
        return new KnowledgeImport(taken, putBeside, beside);
    }

    /// <summary>
    /// The brief of the task that fits what was taken to a project: the project's own part of each skill in its add-on,
    /// a line in its map, and a merge of the two versions where the user had one too.
    /// </summary>
    public static string SetUpBrief(string packName, string packs, KnowledgeImport import)
    {
        var brief = new StringBuilder($"Set up for this project the skills just taken from the pack \"{packName}\".\n\n");
        if (import.Taken.Count > 0)
        {
            brief.Append($"Now in the tool packs ({packs}), shared by all projects:\n");
            foreach (var item in import.Taken) brief.Append($"- {item}\n");
        }

        if (import.PutBeside.Count > 0)
        {
            brief.Append($"\nThese you had changed yourself, so yours stay and the pack's are beside them, in {import.Beside}:\n");
            foreach (var item in import.PutBeside) brief.Append($"- {item}\n");
        }

        brief.Append("""

            1. Read each one's SKILL.md (a script only when a step needs it).
            2. What holds only here (this project's paths, scripts, asset names, numbers) goes into the project's add-on skill
               `<pack>-project` in .agents/skills: make it if it is missing, and say in it to read the pack's skill first.
            3. Where a skill fits work this project does, name it in a line of the project's map.
            """);
        if (import.PutBeside.Count > 0)
        {
            brief.Append("\n4. For each skill with two versions, write one with the lessons of both, nothing lost, into the project's copy " +
                         ".agents/skills/<name>: Link then offers to share it with all games.");
        }

        brief.Append("\n\nChange no other files. End with what you set up, one line per skill.");
        return brief.ToString();
    }

    /// <summary>The skills and roles a pack has, by their path in the packs folder.</summary>
    private static IEnumerable<string> ItemsOf(ToolPack pack)
    {
        var skills = Path.Combine(pack.Folder, "skills");
        if (Directory.Exists(skills))
        {
            foreach (var folder in Directory.GetDirectories(skills).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(folder, "SKILL.md"))) yield return $"{pack.Id}/skills/{Path.GetFileName(folder)}";
            }
        }

        var roles = Path.Combine(pack.Folder, "roles");
        if (!Directory.Exists(roles)) yield break;
        foreach (var role in Directory.GetFiles(roles, "*.md").Order(StringComparer.OrdinalIgnoreCase)) yield return $"{pack.Id}/roles/{Path.GetFileName(role)}";
    }

    /// <summary>An item's files with their names in the packs folder, without Python's caches and hidden files.</summary>
    private static IEnumerable<(string Path, string Name)> Files(string packs, string item)
    {
        var path = Local(packs, item);
        if (File.Exists(path)) return [(path, item)];
        return Directory.GetFiles(path, "*", Deep)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}") && !file.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(file => (file, Path.GetRelativePath(packs, file).Replace('\\', '/')));
    }

    private static string Local(string root, string name) => Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));

    private static bool Differs(string file, string other) => !File.Exists(other) || Hash(file) != Hash(other);

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static string DescriptionFile(string path) => File.Exists(path) ? path : Path.Combine(path, "SKILL.md");

    private static string Description(string fileOrText)
    {
        var text = File.Exists(fileOrText) ? File.ReadAllText(fileOrText) : fileOrText;
        var (fields, _) = ProjectSkills.FrontMatter(text);
        return fields.GetValueOrDefault("description") ?? fields.GetValueOrDefault("name") ?? string.Empty;
    }

    /// <summary>Up to three lines of an item that look personal, as <c>file:line why</c>.</summary>
    private static IReadOnlyList<string> Personal(string packs, string item, IReadOnlyCollection<string> words)
    {
        var found = new List<string>();
        foreach (var (path, name) in Files(packs, item).Where(file => TextFiles.Contains(Path.GetExtension(file.Path))))
        {
            // A skill's file by its name in the skill (SKILL.md, scripts/run.py); a role by its own.
            var inside = name.Length > item.Length ? name[(item.Length + 1)..] : Path.GetFileName(name);
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length && found.Count < 3; index++)
            {
                if (Why(lines[index], words) is { } why) found.Add($"{inside}:{index + 1} {why}");
            }
        }

        return found;
    }

    /// <summary>Why a line looks personal: a user's folder, an email, a key or token, or one of the words; null when it does not.</summary>
    public static string? Why(string line, IReadOnlyCollection<string> words)
    {
        if (UserFolder().IsMatch(line)) return "a user's folder";
        if (Email().IsMatch(line)) return "an email";
        if (Secret().IsMatch(line)) return "a key or token";
        var word = words.FirstOrDefault(word => Regex.IsMatch(line, $@"(?<![\w]){Regex.Escape(word)}(?![\w])", RegexOptions.IgnoreCase));
        return word is null ? null : $"\"{word}\"";
    }

    [GeneratedRegex(@"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s""'`]+|/[a-z]/Users/[^/\s""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+\.[a-z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:sk-[\w-]{20,}|gh[pousr]_\w{20,}|xox[abpr]-[\w-]{10,}|AKIA[0-9A-Z]{16}|\d{8,10}:[\w-]{35})\b|(?:api[_-]?key|token|secret|password)\s*[:=]\s*['""]?[\w\-./+]{12,}", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    private static string Readme(IReadOnlyList<KnowledgeItem> items)
    {
        var readme = new StringBuilder("""
            # Skills from Link

            What someone's agents learned while they worked, as Agent Skills: each folder with a SKILL.md is one skill.

            ## With Link
            Studio → Harness → Share → Open a pack…: Link shows what is new to you and adds it to your tool packs, then
            offers to set it up for your project.

            ## Without Link
            - Claude Code: copy a skill's folder from `packs/<tool>/skills/` into `~/.claude/skills/` (every project) or
              `<project>/.claude/skills/`.
            - Codex and other agents that read Agent Skills: into `<project>/.agents/skills/`.
            - A skill that names another one (to read first) needs that one too.

            ## Inside

            """);
        foreach (var item in items) readme.Append($"- `{item.Item}`{(item.Description.Length > 0 ? " — " + item.Description : string.Empty)}\n");
        return readme.ToString();
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
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
    /// The pack's entries under <c>packs/</c>: each with its item (null for a pack.md) and its name in the packs folder.
    /// The README and the manifest are not taken. A pack of another kind or version, or an entry whose path is rooted,
    /// goes up, is empty, is no file name or starts a pack with a dot, is refused whole.
    /// </summary>
    private static IReadOnlyList<(string? Item, string Name, ZipArchiveEntry Entry)> Entries(ZipArchive zip)
    {
        if (ReadManifest(zip) is not { Kind: Kind, Format: Format }) throw new InvalidDataException("This is not a LINK knowledge pack of this version.");
        var entries = new List<(string?, string, ZipArchiveEntry)>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var folder = name.EndsWith('/');
            var parts = (folder ? name[..^1] : name).Split('/');
            if (parts.Any(part => part is "" or "." or ".." || part.IndexOfAny(NotInName) >= 0) || parts is ["packs", var dotted, ..] && dotted.StartsWith('.'))
            {
                throw new InvalidDataException($"The pack has an entry that is not a safe path: {name}");
            }

            if (folder) continue;
            var inPacks = string.Join('/', parts.Skip(1));
            switch (parts)
            {
                case ["packs", _, "pack.md"]:
                    entries.Add((null, inPacks, entry));
                    break;
                case ["packs", var pack, "skills", var skill, _, ..]:
                    entries.Add(($"{pack}/skills/{skill}", inPacks, entry));
                    break;
                case ["packs", _, "roles", var role] when role.EndsWith(".md", StringComparison.OrdinalIgnoreCase):
                    entries.Add((inPacks, inPacks, entry));
                    break;
            }
        }

        return entries;
    }

    private static KnowledgeState StateOf(string item, IEnumerable<(string? Item, string Name, ZipArchiveEntry Entry)> entries, string packs, IReadOnlyDictionary<string, string> seeded)
    {
        var path = Local(packs, item);
        if (!File.Exists(path) && !Directory.Exists(path)) return KnowledgeState.Added;
        var state = KnowledgeState.Same;
        foreach (var (_, name, entry) in entries)
        {
            var target = Local(packs, name);
            if (!File.Exists(target))
            {
                state = KnowledgeState.Updates;
                continue;
            }

            using var stream = entry.Open();
            var ours = Convert.ToHexString(SHA256.HashData(stream));
            var theirs = Hash(target);
            if (ours == theirs) continue;
            // A file Link put there and nobody changed since is Link's: the pack's lesson replaces it.
            if (!seeded.TryGetValue(name, out var put) || put != theirs) return KnowledgeState.BothChanged;
            state = KnowledgeState.Updates;
        }

        return state;
    }

    /// <summary>Writes an entry under <paramref name="root"/>, never outside it.</summary>
    private static void Extract(ZipArchiveEntry entry, string root, string name)
    {
        var full = Path.GetFullPath(root);
        var target = Path.GetFullPath(Local(full, name));
        if (!target.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The pack has an entry outside the packs: {name}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
        entry.ExtractToFile(target, overwrite: true);
    }
}
