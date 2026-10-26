namespace Orbit.Shell.ViewModels;

public sealed class TodoItemVm
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public bool Done { get; init; }
    public string? Reason { get; init; }
    public string Glyph => Done ? "✓" : "○";
    public bool HasReason => !string.IsNullOrWhiteSpace(Reason);
    public double TitleOpacity => Done ? 0.55 : 1.0;
}
