namespace Link.Desktop.Models;

public sealed class LinkProject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string DataFolder { get; init; }
    public string CurrentTask { get; set; } = string.Empty;

    public override string ToString() => Name;
}
