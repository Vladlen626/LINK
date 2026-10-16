using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Link.Desktop.Services;

/// <summary>
/// Claude Code's accounts: the user's own (<c>~/.claude</c>) and the others Link keeps, each a config folder of its own
/// that Claude Code is pointed at by <c>CLAUDE_CONFIG_DIR</c>, with its own sign-in. Every account's <c>projects</c>
/// is a junction to the user's own, so a task's session goes on under another account (one at its limit, say).
/// </summary>
public static class ClaudeAccounts
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Link", "ClaudeAccounts");

    private static string OwnProjects => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>
    /// The folder of a workspace's memory where it exists. Through an account's junction Claude Code takes it for a folder
    /// outside its own and refuses to write it (agents met "Not allowed: Write" there), so a run is given it.
    /// </summary>
    public static string? MemoryFolder(string workspace) =>
        Path.Combine(OwnProjects, Regex.Replace(workspace, "[^A-Za-z0-9]", "-"), "memory") is var folder && Directory.Exists(folder) ? folder : null;

    /// <summary>The other accounts, by their folders' names.</summary>
    public static IReadOnlyList<string> Names() =>
        Directory.Exists(Root) ? [.. Directory.GetDirectories(Root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)] : [];

    /// <summary>What Claude Code's process gets to run under an account; nothing for the user's own.</summary>
    public static IReadOnlyDictionary<string, string> Variables(string? account) =>
        account is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = Path.Combine(Root, account) };

    /// <summary>A new account's folder, "Account 2" and on, its sessions shared with the user's own; returns its name.</summary>
    public static async Task<string> AddAsync()
    {
        var names = Names();
        var name = Enumerable.Range(2, 100).Select(number => $"Account {number}").First(next => !names.Contains(next, StringComparer.OrdinalIgnoreCase));
        var folder = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        Directory.CreateDirectory(OwnProjects);
        // A junction needs no administrator, unlike a symbolic link, and .NET makes none: cmd's mklink does.
        using var junction = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(folder, "projects")}\" \"{OwnProjects}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (junction is not null) await junction.WaitForExitAsync();
        return name;
    }

    /// <summary>
    /// <c>claude auth login</c> for an account in a console of its own, which opens the browser; done when the console
    /// closes, signed in or not.
    /// </summary>
    public static async Task SignInAsync(string executable, string? account)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c \"\"{executable}\" auth login\"") { UseShellExecute = false };
        foreach (var (name, value) in Variables(account)) info.Environment[name] = value;
        using var console = Process.Start(info);
        if (console is not null) await console.WaitForExitAsync();
    }
}
