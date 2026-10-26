namespace Orbit.Contracts;

/// <summary>One agents/*.json file that failed to load (visible to Shell diagnostics).</summary>
public sealed class AgentManifestLoadError
{
    public required string File { get; init; }
    public required string Error { get; init; }
}
