using System;
using System.Text.Json;
using Link.Guard;

// Claude Code's PreToolUse hook: the call as JSON on stdin; exit 2 refuses it, and stderr tells the agent why.
// Anything this cannot read lets the call run: the guard never stops work by its own failure.
try
{
    using var input = JsonDocument.Parse(Console.OpenStandardInput());
    if (HarnessGuard.Check(input.RootElement) is { } why)
    {
        Console.Error.WriteLine(why);
        return 2;
    }
}
catch (Exception exception) when (exception is JsonException or System.IO.IOException)
{
}

return 0;
