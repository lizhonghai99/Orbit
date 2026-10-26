namespace Orbit.Shell.ViewModels;

/// <summary>One Cursor-style activity line (Read / Searched / Ran …).</summary>
public sealed class ToolCallRowVm
{
    public required string Name { get; init; }
    public required string StatusLabel { get; init; }
    public required string Verb { get; init; }
    public required string Detail { get; init; }
    public string ArgsText { get; init; } = "";
    public string ResultText { get; init; } = "";
    public string Line => string.IsNullOrWhiteSpace(Detail)
        ? $"{Verb} {Name}"
        : $"{Verb} {Detail}";
    public string Header => Line;
    public bool HasArgs => !string.IsNullOrWhiteSpace(ArgsText);
    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);
}
