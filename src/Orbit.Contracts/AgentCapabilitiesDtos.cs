namespace Orbit.Contracts;

/// <summary>Aggregated read-only capability profile for Shell (Runtime facade).</summary>
public sealed class AgentCapabilitiesDto
{
    public required string AgentId { get; init; }
    public bool Offline { get; init; }
    public string? Model { get; init; }
    public string? Reasoning { get; init; }
    public AgentHarnessDto? Harness { get; init; }
    public IReadOnlyList<CapabilitySkillDto> Skills { get; init; } = [];
    public IReadOnlyList<CapabilityToolDto> Tools { get; init; } = [];
    public IReadOnlyList<CapabilityMcpServerDto> Mcp { get; init; } = [];
}

public sealed class CapabilitySkillDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>builtin | user</summary>
    public string Source { get; init; } = "builtin";
    public bool Enabled { get; init; } = true;
    public bool HasScripts { get; init; }
    public string? Sha256 { get; init; }
    public string? Path { get; init; }
}

public sealed class CapabilityToolDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool RequiresApproval { get; init; }
    /// <summary>domain | harness — this round catalogs domain tools only.</summary>
    public string Source { get; init; } = "domain";
    /// <summary>False when listed in the persisted disabled-tools profile (still visible in schema).</summary>
    public bool Enabled { get; init; } = true;
}

public sealed class CapabilityMcpServerDto
{
    public required string Id { get; init; }
    public string? Transport { get; init; }
    public string Status { get; init; } = "unconfigured";
    public int ToolCount { get; init; }
    public bool Trusted { get; init; }
    public bool Enabled { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> ToolNames { get; init; } = [];
}


/// <summary>Full replacement lists for disabled skills/tools (not a diff).</summary>
public sealed class CapabilityPatchRequest
{
    public IReadOnlyList<string>? DisabledSkills { get; init; }
    public IReadOnlyList<string>? DisabledTools { get; init; }
}

public sealed class CapabilityPatchResult
{
    public IReadOnlyList<string> Applied { get; init; } = [];
    /// <summary>This round: mcp/model would appear here — hot-swap not implemented.</summary>
    public IReadOnlyList<string> RequiresRestart { get; init; } = [];
    public int AffectedSessions { get; init; }
}

/// <summary>Persisted / in-process enforcement profile (skills &amp; tools disable lists).</summary>
public sealed class CapabilityProfileDto
{
    public IReadOnlyList<string> DisabledSkills { get; init; } = [];
    public IReadOnlyList<string> DisabledTools { get; init; } = [];
}

