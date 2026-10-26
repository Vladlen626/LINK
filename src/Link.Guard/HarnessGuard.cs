using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Link.Guard;

/// <summary>
/// The agent rules a script can hold at the moment they matter (agent-rules.md): before a tool runs, Claude Code asks
/// this, and a call that breaks a rule is refused with what to do instead. A rule held here leaves Claude's rules text:
/// no tokens at every session's start, and a weak model cannot forget it. Codex has no such hook and keeps the text.
/// </summary>
public static partial class HarnessGuard
{
    /// <summary>A file this long is read by a range, after a grep: the whole of it stays in the session.</summary>
    public const int WholeReadLines = 600;

    /// <summary>A wait this long holds the run on a tool that may never come back.</summary>
    public const int LongestWait = 60;

    /// <summary>Why the call breaks a rule, said to the agent; null when it may run.</summary>
    public static string? Check(JsonElement input)
    {
        var tool = Text(input, "tool_name");
        if (!input.TryGetProperty("tool_input", out var args) || args.ValueKind != JsonValueKind.Object) return null;
        var workspace = Text(input, "cwd");
        return tool switch
        {
            "Read" => WholeRead(args),
            "Write" => Leftover(Text(args, "file_path"), workspace),
            "Bash" or "PowerShell" => LongWait(Text(args, "command")) ?? Encoding(Text(args, "command"), tool == "PowerShell"),
            _ => null
        };
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
}
