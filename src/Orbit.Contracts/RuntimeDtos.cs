namespace Orbit.Contracts;

public sealed class RuntimeHealth
{
    public string Status { get; init; } = "ok";
    /// <summary>Informational package version (<see cref="OrbitProductVersion.Current"/>).</summary>
    public string Version { get; init; } = OrbitProductVersion.Current;
    public DateTimeOffset StartedAt { get; init; }
    public string RepoRoot { get; init; } = "";
}
