using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Link.Guard;

/// <summary>
/// What a shell command does, by the first program it runs: looks around or runs code. Link's run parsers count by it,
/// and the guard by the same words, so a search is one thing in the log and in the hook.
/// </summary>
public static partial class ShellCommand
{
    // Programs that only find, list or read files, by the name a shell command starts with.
    private static readonly HashSet<string> SearchPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "rg", "grep", "egrep", "fgrep", "findstr", "find", "fd", "ls", "dir", "tree", "cat", "type", "head", "tail", "more",
        "Get-ChildItem", "gci", "Select-String", "sls", "Get-Content", "gc"
    };

    // Programs that run code, by the same first name: interpreters, builds, package runners and DCC apps run headless.
    private static readonly HashSet<string> CodeRunners = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "python", "python3", "py", "uv", "uvx", "dotnet", "npm", "npx", "pnpm", "yarn", "bun", "deno", "cargo", "go",
        "make", "cmake", "msbuild", "gradle", "mvn", "java", "pytest", "blender", "UnrealEditor", "UnrealEditor-Cmd"
    };

    private static readonly HashSet<string> FolderChanges = new(StringComparer.OrdinalIgnoreCase) { "cd", "Set-Location", "sl", "pushd", "Push-Location" };

    /// <summary>
    /// Whether a shell command only looks around: the first program it runs, after any change of folder, finds, lists
    /// or reads files. Codex searches only this way; Claude Code mostly with its own tools.
    /// </summary>
    public static bool IsSearch(string command) => FirstCommand(command) is { } words &&
        (ProgramOf(words[0]).Equals("git", StringComparison.OrdinalIgnoreCase)
            ? words.Length > 1 && words[1] is "grep" or "ls-files"
            : SearchPrograms.Contains(ProgramOf(words[0])));

    /// <summary>Whether a shell command runs code (a script, a build, a test): work, as an edit is, not a look around.</summary>
    public static bool RunsCode(string command) => FirstCommand(command) is { } words && CodeRunners.Contains(ProgramOf(words[0]));

    /// <summary>
    /// The words of the first command a shell line runs, past changes of folder, variables set for it and a time limit
    /// (<c>timeout 280 node check.mjs</c>).
    /// </summary>
    private static string[]? FirstCommand(string command)
    {
        foreach (var part in CommandSeparators().Split(command))
        {
            var words = part.Trim().TrimStart('(', '&', '@', '$').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(word => Assignment().IsMatch(word))
                .ToArray();
            if (words.Length > 2 && ProgramOf(words[0]).Equals("timeout", StringComparison.OrdinalIgnoreCase)) words = words[2..];
            if (words.Length == 0 || FolderChanges.Contains(ProgramOf(words[0]))) continue;
            return words;
        }

        return null;
    }

    private static string ProgramOf(string word) => Path.GetFileNameWithoutExtension(word.Trim('"', '\''));

    // Commands one after another: ; && || and new lines. A pipe is not one: what it feeds belongs to the first program.
    [GeneratedRegex(@"&&|\|\||;|\n")]
    private static partial Regex CommandSeparators();

    // A variable set for the command that follows it: MSYS_NO_PATHCONV=1 node a.mjs.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();
}
