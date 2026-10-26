namespace Orbit.Contracts;

/// <summary>Host workspace policy on <see cref="AgentManifest.Workspace"/>.</summary>
public static class OrbitWorkspacePolicy
{
    public const string Required = "required";
    public const string Optional = "optional";
    public const string None = "none";

    public static string Normalize(string? raw)
    {
        var p = (raw ?? Optional).Trim().ToLowerInvariant();
        return p is Required or None ? p : Optional;
    }

    public static bool IsRequired(string? raw) =>
        string.Equals(Normalize(raw), Required, StringComparison.Ordinal);

    public static bool IsNone(string? raw) =>
        string.Equals(Normalize(raw), None, StringComparison.Ordinal);

    public static bool ShouldInjectWorkspace(string? raw, bool hasActiveWorkspace) =>
        hasActiveWorkspace && !IsNone(raw);
}
