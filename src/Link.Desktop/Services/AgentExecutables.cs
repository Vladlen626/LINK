using Link.Desktop.Models;

namespace Link.Desktop.Services;

/// <summary>
/// Finds an agent's CLI: the path chosen in Settings, then PATH, then where its installers put it. A real .exe is
/// preferred; a .cmd wrapper (an npm install) is used only when there is nothing else. The custom model runs through
/// Claude Code, so it finds claude.
/// </summary>
public static class AgentExecutables
{
    public static string? Find(AgentProviderKind provider, string? chosenPath)
    {
        if (chosenPath is not null) return File.Exists(chosenPath) ? chosenPath : null;
        var name = provider.RunsOnClaudeCode() ? "claude" : "codex";
        var inPath = SearchPath(name + ".exe");
        if (inPath is not null) return inPath;
        var installed = InstallPlaces(provider).FirstOrDefault(File.Exists);
        return installed ?? SearchPath(name + ".cmd");
    }

    private static string? SearchPath(string fileName) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(folder => SafeCombine(folder, fileName))
        .FirstOrDefault(path => path is not null && File.Exists(path));

    private static IEnumerable<string> InstallPlaces(AgentProviderKind provider)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (provider.RunsOnClaudeCode())
        {
            // The native installer, WinGet, then the copy the Claude desktop app keeps for itself, newest first.
            yield return Path.Combine(profile, ".local", "bin", "claude.exe");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe");
            // The Claude app is packaged (MSIX): Windows keeps its Roaming folder under Packages, and only the app
            // itself sees it at the usual place.
            var packaged = Directory.Exists(Path.Combine(local, "Packages"))
                ? Directory.GetDirectories(Path.Combine(local, "Packages"), "Claude_*")
                    .Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude", "claude-code"))
                : [];
            foreach (var root in packaged.Prepend(Path.Combine(roaming, "Claude", "claude-code")))
            {
                foreach (var bundled in Newest(root, "claude.exe")) yield return bundled;
            }

            // npm's claude.cmd only starts this exe; through cmd.exe a rule such as Bash(a && b > c) cannot pass.
            yield return Path.Combine(roaming, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        }
        else
        {
            yield return Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "codex.exe");
            // npm's codex.cmd starts this exe through node; the exe itself needs no node.
            yield return Path.Combine(roaming, "npm", "node_modules", "@openai", "codex", "node_modules",
                "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        }
    }

    private static IEnumerable<string> Newest(string root, string fileName)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Select(folder => (Folder: folder, Version: Version.TryParse(Path.GetFileName(folder), out var version) ? version : null))
            .Where(entry => entry.Version is not null)
            .OrderByDescending(entry => entry.Version)
            // The exe lies in the version's folder or, as the app keeps it now, in a folder of its own under it.
            .SelectMany(entry => Directory.GetDirectories(entry.Folder).Prepend(entry.Folder))
            .Select(folder => Path.Combine(folder, fileName));
    }

    private static string? SafeCombine(string folder, string fileName)
    {
        try
        {
            return Path.Combine(folder.Trim('"'), fileName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
