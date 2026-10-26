namespace Orbit.Contracts;

/// <summary>One Harness module as exposed by Agent <c>/health</c>.</summary>
public sealed class HarnessModuleDto
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>provider | middleware | pipeline</summary>
    public required string Kind { get; init; }
    public bool Enabled { get; init; }
    public string? Detail { get; init; }
}

public sealed class AgentHarnessDto
{
    /// <summary>e.g. hand-assembled Standard+FileAccess (not AsHarnessAgent).</summary>
    public string Style { get; init; } = "hand-assembled";
    public IReadOnlyList<HarnessModuleDto> Modules { get; init; } = [];

    /// <summary>Unknown <c>harness</c> / <c>ORBIT_HARNESS</c> tokens that were ignored.</summary>
    public IReadOnlyList<string> IgnoredTokens { get; init; } = [];
}

/// <summary>Agent process health including Harness capability profile.</summary>
public sealed class AgentProcessHealthDto
{
    public string Status { get; init; } = "ok";
    public string AgentId { get; init; } = "";
    public string Protocol { get; init; } = "ag-ui";
    public string? AguiPath { get; init; }
    public bool Ready { get; init; }
    public string Chat { get; init; } = "";
    public string? Model { get; init; }
    /// <summary>Effective reasoning label: default | none | low | medium | high | …</summary>
    public string? Reasoning { get; init; }
    public AgentHarnessDto? Harness { get; init; }
    public IReadOnlyList<SkillSummaryDto> Skills { get; init; } = [];
    /// <summary>Domain extras (browser profile, login URL, …).</summary>
    public Dictionary<string, string>? Tools { get; init; }

    /// <summary>Host workspace path injected as <c>ORBIT_WORKSPACE</c>.</summary>
    public string? Workspace { get; init; }

    /// <summary>Host workspace id injected as <c>ORBIT_WORKSPACE_ID</c>.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>True when this process enforces <c>X-Orbit-Token</c> (manual starts without token are open).</summary>
    public bool AuthRequired { get; init; }
}
