namespace Orbit.Contracts;

public sealed class SkillSummaryDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>builtin | user</summary>
    public string Source { get; init; } = "builtin";
    public bool Enabled { get; init; } = true;
    public bool HasScripts { get; init; }
    /// <summary>Lowercase hex SHA-256 of SKILL.md contents.</summary>
    public string? Sha256 { get; init; }
    public string? Path { get; init; }
}

