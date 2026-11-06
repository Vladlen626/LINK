using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Link.Guard;

/// <summary>
/// What the guard remembers of one agent session between its calls (it starts anew for each): the searches since the
/// last piece of work and the images opened. A small file per session in the temp folder; a session the guard cannot
/// read or write is simply not remembered.
/// </summary>
internal sealed class GuardSession
{
    // Sessions end without telling the guard: a file this old belongs to none that still runs.
    private static readonly TimeSpan Kept = TimeSpan.FromDays(3);

    private readonly string? _file;

    private GuardSession(string? file) => _file = file;

    /// <summary>Reads, finds or lists files since the last edit, script or MCP call.</summary>
    public int Searches { get; set; }

    /// <summary>The tool calls the guard let run, and of them the edits.</summary>
    public int Calls { get; set; }

    public int Edits { get; set; }

    /// <summary>An edit went to the project's map, a skill or the agent instructions: the session kept what it learned.</summary>
    public bool Learned { get; set; }

    /// <summary>The session was asked once, before it ended, what to keep.</summary>
    public bool Reflected { get; set; }

    /// <summary>Each image opened, by its full path, with the time it was written then.</summary>
    public Dictionary<string, long> Images { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The call refused once already, which runs if the agent asks for it again.</summary>
    public string? Refused { get; set; }

    public static string Folder => Path.Combine(Path.GetTempPath(), "link-guard");

    public static GuardSession Load(string? sessionId)
    {
        var name = new string((sessionId ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        if (name.Length == 0) return new GuardSession(null);
        var session = new GuardSession(Path.Combine(Folder, name + ".txt"));
        try
        {
            if (!File.Exists(session._file)) return session;
            foreach (var line in File.ReadAllLines(session._file))
            {
                var parts = line.Split(' ', 3);
                if (parts is ["searches", var count] && int.TryParse(count, out var searches)) session.Searches = searches;
                else if (parts is ["calls", var made] && int.TryParse(made, out var calls)) session.Calls = calls;
                else if (parts is ["edits", var changed] && int.TryParse(changed, out var edits)) session.Edits = edits;
                else if (line == "learned") session.Learned = true;
                else if (line == "reflected") session.Reflected = true;
                else if (parts is ["refused", _, ..]) session.Refused = line["refused ".Length..];
                else if (parts is ["image", var ticks, var path] && long.TryParse(ticks, out var written)) session.Images[path] = written;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return session;
    }

    public void Save()
    {
        if (_file is null) return;
        var lines = new List<string> { $"searches {Searches}", $"calls {Calls}", $"edits {Edits}" };
        if (Learned) lines.Add("learned");
        if (Reflected) lines.Add("reflected");
        if (Refused is not null) lines.Add("refused " + Refused);
        lines.AddRange(Images.Select(image => $"image {image.Value} {image.Key}"));
        try
        {
            var first = !File.Exists(_file);
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(_file, lines);
            if (first) Forget();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Once per new session, the files of sessions long over go.
    private static void Forget()
    {
        foreach (var file in Directory.EnumerateFiles(Folder, "*.txt"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > Kept) File.Delete(file);
        }
    }
}
