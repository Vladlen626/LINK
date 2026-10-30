using System;
using System.Text.Json;
using Link.Guard;

// Claude Code's PreToolUse and Stop hook: the call (or the end) as JSON on stdin; exit 2 refuses it, and stderr tells the
// agent why. Anything this cannot read lets the call run: the guard never stops work by its own failure.
try
{
    using var input = JsonDocument.Parse(Console.OpenStandardInput());
    var root = input.RootElement;
    var stop = root.TryGetProperty("hook_event_name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "Stop";
    if ((stop ? HarnessGuard.Stop(root) : HarnessGuard.Check(root)) is { } why)
    {
        Console.Error.WriteLine(why);
        return 2;
    }
}
catch (Exception exception) when (exception is JsonException or System.IO.IOException)
{
}

return 0;
