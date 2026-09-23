namespace Link.Desktop.Models;

/// <summary>A capture endpoint. A null <see cref="Id"/> stands for the Windows default microphone.</summary>
public sealed record AudioInputDevice(string? Id, string Name)
{
    public override string ToString() => Name;
}
