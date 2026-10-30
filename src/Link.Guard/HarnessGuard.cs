using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Link.Guard;

/// <summary>
/// The agent rules a script can hold at the moment they matter (agent-rules.md): before a tool runs, Claude Code asks
/// this, and a call that breaks a rule is refused with what to do instead; before the session ends, it may be asked once
/// what to keep. A rule held here leaves Claude's rules text: no tokens at every session's start, and a weak model cannot
/// forget it. Codex runs without the guard and keeps the text.
/// </summary>
public static partial class HarnessGuard
{
    /// <summary>A file this long is read by a range, after a grep: the whole of it stays in the session.</summary>
    public const int WholeReadLines = 600;

    /// <summary>A wait this long holds the run on a tool that may never come back.</summary>
    public const int LongestWait = 60;

    /// <summary>A command this long is a script: written to a file once, it is changed by edits, not sent again whole.</summary>
    public const int LongestCommand = 40;

    /// <summary>
    /// Reads and searches since the last piece of work after which the agent is asked to say what it knows. Link marks a
    /// run as searching at 12 in a row (AgentLoopDetector); the guard steps in a little later, so a short look never
    /// pays a turn.
    /// </summary>
    public const int SearchesWithoutWork = 15;

    /// <summary>Why the call breaks a rule, said to the agent; null when it may run.</summary>
    public static string? Check(JsonElement input)
    {
        var tool = Text(input, "tool_name") ?? string.Empty;
        if (!input.TryGetProperty("tool_input", out var args) || args.ValueKind != JsonValueKind.Object) return null;
        var workspace = Text(input, "cwd");
        var command = Text(args, "command");
        var edited = tool switch
        {
            "Write" or "Edit" or "MultiEdit" => Text(args, "file_path"),
            "NotebookEdit" => Text(args, "notebook_path"),
            _ => null
        };
        if (Protected(edited, workspace) is { } kept) return kept;

        // No-stop asks nothing and refuses nothing else: its run is the user's "allow all", and a nudge reads as a refusal.
        var noStop = Text(input, "permission_mode") == "bypassPermissions";
        var why = noStop ? null : tool switch
        {
            "Read" => WholeRead(args),
            "Write" => Leftover(edited, workspace),
            "Bash" or "PowerShell" => LongWait(command) ?? Encoding(command, tool == "PowerShell") ?? LongScript(command, workspace),
            _ => null
        };
        if (why is not null) return why;

        // The session is still counted for the retrospective before it ends.
        var session = GuardSession.Load(Text(input, "session_id"));
        why = Remembered(session, tool, args, command, subagent: Text(input, "agent_id") is not null);
        session.Save();
        return noStop ? null : why;
    }

    /// <summary>
    /// Tool calls after which a session that changed files is long work: before it ends, it is asked once what to keep.
    /// Hermes' retrospective after a task, held by code: the rules' "get stronger" alone was mostly skipped.
    /// </summary>
    public const int LongWork = 40;

    /// <summary>
    /// Claude Code's Stop hook: why the session should not end yet, said to it; null when it may. Once per session, after
    /// long work that changed files and kept nothing in the project's map or skills.
    /// </summary>
    public static string? Stop(JsonElement input)
    {
        if (input.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True) return null;
        var session = GuardSession.Load(Text(input, "session_id"));
        if (session.Reflected || session.Learned || session.Edits == 0 || session.Calls < LongWork) return null;
        session.Reflected = true;
        session.Save();
        return $"Before you end: this run made {session.Calls} tool calls and kept nothing for the next one. If it proved " +
            "something the next agent would need and could not see at once (a project fact, a way of doing repeated work and " +
            "its pitfalls, a correction the user gave that holds beyond this task), put it in the project's map or the skill it " +
            "belongs to now, in a few short lines. Nothing like that: change nothing. Then end with the same answer you gave, " +
            "in full: the user sees only your last message.";
    }

    // The rules that need the session's past: an image opened again unchanged, and searching on without doing.
    private static string? Remembered(GuardSession session, string tool, JsonElement args, string? command, bool subagent)
    {
        session.Calls++;
        var shell = tool is "Bash" or "PowerShell";
        if (tool is "Edit" or "MultiEdit" or "NotebookEdit" or "Write")
        {
            session.Edits++;
            if (Text(args, "file_path") is { } edited && Knowledge().IsMatch(edited.Replace('\\', '/'))) session.Learned = true;
        }

        if (tool is "Edit" or "MultiEdit" or "NotebookEdit" or "Write" || tool.StartsWith("mcp__", StringComparison.Ordinal) ||
            (shell && command is not null && ShellCommand.RunsCode(command)))
        {
            session.Searches = 0;
            return null;
        }

        // A subagent has a context of its own, and a survey's work is to search: the counts are the main agent's.
        if (subagent) return null;
        if (tool == "Read" && Text(args, "file_path") is { } file && Binary().IsMatch(file)) return SameImage(session, file);
        if (tool is not ("Read" or "Grep" or "Glob" or "LS") && !(shell && command is not null && ShellCommand.IsSearch(command))) return null;
        if (++session.Searches < SearchesWithoutWork) return null;
        session.Searches = 0;
        return $"{SearchesWithoutWork} reads and searches since your last change. Say in two lines what you know now and " +
            "what you will change; then go on, and this call runs when you make it again.";
    }

    // An image costs its tokens again each time it is opened; asked for twice in a row, it runs (after a compaction, say).
    private static string? SameImage(GuardSession session, string file)
    {
        var path = Path.GetFullPath(file);
        var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        var key = "image " + path;
        if (session.Images.TryGetValue(path, out var seen) && seen == written && session.Refused != key)
        {
            session.Refused = key;
            return $"You opened {Path.GetFileName(path)} already in this session and it has not changed since: use what you saw. " +
                "If you need it again, open it again and it runs.";
        }

        session.Images[path] = written;
        session.Refused = null;
        return null;
    }

    // A long command is a script, and each next version of it would be sent whole again; a heredoc that makes a new
    // file is the Write tool by another name and passes.
    private static string? LongScript(string? command, string? workspace)
    {
        if (command is null) return null;
        var lines = command.Split('\n').Length;
        if (lines <= LongestCommand) return null;
        if (NewFile().Match(command) is { Success: true } match && workspace is not null && !File.Exists(WindowsPath(match.Groups["file"].Value, workspace)))
        {
            return null;
        }

        return $"A command of {lines} lines stays in the session, and so would each next version of it: write the script to a " +
            "file with Write, run the file, and change it with Edit.";
    }

    // Git Bash writes C:\x as /c/x.
    private static string WindowsPath(string file, string workspace)
    {
        file = file.Trim('"', '\'');
        if (file.Length > 2 && file[0] == '/' && char.IsAsciiLetter(file[1]) && file[2] == '/') file = $"{file[1]}:{file[2..]}";
        return Path.GetFullPath(file, workspace);
    }

    private static string? WholeRead(JsonElement args)
    {
        if (args.TryGetProperty("offset", out _) || args.TryGetProperty("limit", out _)) return null;
        if (Text(args, "file_path") is not { } file || Binary().IsMatch(file)) return null;
        var lines = Lines(file);
        return lines > WholeReadLines
            ? $"{Path.GetFileName(file)} is {lines} lines, and a whole read stays in the session for every later request: Grep for what you need, then Read that range with offset and limit. If you need all of it, read it in ranges."
            : null;
    }

    /// <summary>
    /// Folders a task's agent never edits, whatever its access: Link's own data (the ChatGPT session among it), the agents'
    /// own settings and hooks (the guard's among them) and the shared skill packs, which serve every project. A workspace
    /// inside one of them is that folder's own task and may.
    /// </summary>
    public static IReadOnlyList<(string Folder, string What)> ProtectedFolders { get; } =
    [
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Link"), "Link's own data"),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "Claude Code's own settings"),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "Codex's own settings"),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LinkPacks"), "the skill packs every project shares")
    ];

    private static string? Protected(string? file, string? workspace)
    {
        if (file is null || workspace is null) return null;
        var full = Path.GetFullPath(WindowsPath(file, workspace));
        foreach (var (folder, what) in ProtectedFolders)
        {
            if (Inside(full, folder) && !Inside(Path.GetFullPath(workspace), folder))
            {
                return $"{full} is in {what}, which a task never edits. Leave it, and say at the end of your answer what " +
                    "should change there and why: the user does it.";
            }
        }

        return null;
    }

    private static bool Inside(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Equals(folder, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Leftover(string? file, string? workspace)
    {
        if (file is null || workspace is null) return null;
        var full = Path.GetFullPath(file, workspace);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = full[root.Length..].Replace('\\', '/');
        return LeftoverFile().IsMatch(relative)
            ? $"{relative} looks like a check's leftover: temporary files (logs, frames, dumps) go to the system's temp folder ({Path.GetTempPath().TrimEnd('\\')}), not the project."
            : null;
    }

    private static string? LongWait(string? command)
    {
        if (command is null) return null;
        var longest = Wait().Matches(command).Select(match => int.TryParse(match.Groups["s"].Value, out var seconds) ? seconds : 0).DefaultIfEmpty(0).Max();
        return longest >= LongestWait
            ? $"A wait of {longest} s: do not wait on a tool for long (an editor in Play, a server that is down). Do what you can without it, then ask the user with a link-ask block."
            : null;
    }

    // Windows PowerShell 5.1 writes ANSI without -Encoding: non-Latin text turns to garbage.
    private static string? Encoding(string? command, bool powerShell)
    {
        if (command is null || (!powerShell && !command.Contains("powershell", StringComparison.OrdinalIgnoreCase))) return null;
        return Writes().IsMatch(command) && !command.Contains("-Encoding", StringComparison.OrdinalIgnoreCase)
            ? "Windows PowerShell 5.1 writes a file without -Encoding as ANSI, and non-Latin text turns to garbage: add -Encoding UTF8 to Set-Content, Add-Content or Out-File."
            : null;
    }

    private static int Lines(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length < WholeReadLines * 20L) return 0;
            using var reader = new StreamReader(file);
            var count = 0;
            while (reader.ReadLine() is not null) count++;
            return count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"\.(png|jpe?g|gif|webp|bmp|pdf|ipynb)$", RegexOptions.IgnoreCase)]
    private static partial Regex Binary();

    // Logs, dumps and temp files anywhere; pictures only at the top, where no asset lives.
    [GeneratedRegex(@"(\.(log|tmp|dmp)$)|(^[^/]+\.(png|jpe?g|bmp)$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeftoverFile();

    [GeneratedRegex(@"(?:\bsleep\s+|Start-Sleep\s+(?:-Seconds\s+|-s\s+)?|\btimeout\s+/t\s+)(?<s>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Wait();

    [GeneratedRegex(@"\b(Set-Content|Add-Content|Out-File)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Writes();

    // Where a session keeps what it learned: the project's map (Link's or the repository's), skills, agent instructions.
    [GeneratedRegex(@"(^|/)(docs/(map|README)\.md|\.agents/skills/.+|\.claude/skills/.+|AGENTS\.md|CLAUDE\.md)$", RegexOptions.IgnoreCase)]
    private static partial Regex Knowledge();

    // cat > file <<'EOF' or tee file <<EOF: a file written from a heredoc.
    [GeneratedRegex(@"(?:\bcat\s*>|\btee\s+)\s*(?<file>[""']?[^\s<|;&""']+[""']?)\s*<<")]
    private static partial Regex NewFile();
}
