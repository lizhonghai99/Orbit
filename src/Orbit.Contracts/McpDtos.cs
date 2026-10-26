namespace Orbit.Contracts;

public sealed class McpServerConfig
{
    public required string Id { get; init; }
    /// <summary>stdio | http</summary>
    public string Transport { get; init; } = "stdio";
    public string? Command { get; init; }
    public List<string> Args { get; init; } = [];
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Url { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Enabled { get; init; } = true;
    public bool Trusted { get; init; }
    public string? ToolPrefix { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    /// <summary>When true and <see cref="ApprovalTools"/> is empty, every tool from this server needs HITL.</summary>
    public bool RequiresApproval { get; init; }
    /// <summary>
    /// Unprefixed MCP tool names that need HITL. When non-empty, only these are wrapped
    /// (overrides blanket <see cref="RequiresApproval"/> for wrapping decisions).
    /// </summary>
    public List<string> ApprovalTools { get; init; } = [];
}

public sealed class McpConfigFile
{
    /// <summary>
    /// Optimistic concurrency token. <c>PUT</c> must send the revision from the last <c>GET</c>;
    /// mismatch → 409 (another editor saved first).
    /// </summary>
    public int Revision { get; set; }

    /// <summary>
    /// Required on PUT: omit / null → 400 (do not treat as wipe). Explicit <c>[]</c> clears all servers.
    /// On disk / after Load, always a non-null list.
    /// </summary>
    public List<McpServerConfig>? Servers { get; set; }
}

public sealed class McpToolDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ServerId { get; init; }
}

public sealed class McpServerStatusDto
{
    public required string Id { get; init; }
    public string? Transport { get; init; }
    /// <summary>connected | disabled | untrusted | error</summary>
    public string Status { get; init; } = "disabled";
    public int ToolCount { get; init; }
    public bool Trusted { get; init; }
    public bool Enabled { get; init; }
    public string? Error { get; init; }
    public List<McpToolDto> Tools { get; init; } = [];
}

/// <summary>Agent <c>GET /mcp</c> payload.</summary>
public sealed class McpStatusResponse
{
    public bool Configured { get; init; }
    public IReadOnlyList<McpServerStatusDto> Servers { get; init; } = [];
}

/// <summary>Runtime <c>PUT /agents/{id}/mcp</c> result.</summary>
public sealed class McpSaveResult
{
    public bool Saved { get; init; }
    public bool RequiresRestart { get; init; } = true;
    public int Servers { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Trust / delete mutation result.</summary>
public sealed class McpMutationResult
{
    public bool RequiresRestart { get; init; } = true;
    public string? ServerId { get; init; }
}

public sealed class McpTrustRequest
{
    public bool Trusted { get; init; }
}
