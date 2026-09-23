namespace Link.Desktop.Models;

public sealed class CurrentApplicationContext
{
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
}
