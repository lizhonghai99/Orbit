namespace Orbit.Contracts;

public sealed class WorkspaceRecord
{
    /// <summary>32-hex id (same shape as session ids).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastOpenedAt { get; init; }
}

public sealed class WorkspaceState
{
    public string? ActiveId { get; set; }
    public List<WorkspaceRecord> Items { get; init; } = [];
}

public sealed class OpenWorkspaceRequest
{
    public required string Path { get; init; }
    public string? Name { get; init; }
    /// <summary>
    /// When true, proceed even if switching will cancel in-flight runs and restart Agents.
    /// Without it, Runtime returns 409 <c>confirm_required</c> when any target Agent is busy.
    /// </summary>
    public bool Confirm { get; init; }
}

public sealed class WorkspaceMutationResult
{
    public WorkspaceRecord? Active { get; init; }
    /// <summary>Agents that needed a restart because their started workspace differed.</summary>
    public List<string> RequiresRestartAgents { get; init; } = [];
    /// <summary>Agents successfully restarted after the mutation.</summary>
    public List<string> RestartedAgents { get; init; } = [];
    /// <summary>agentId → error message for failed restarts.</summary>
    public Dictionary<string, string> RestartFailures { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
