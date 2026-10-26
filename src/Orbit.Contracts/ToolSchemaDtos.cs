using System.Text.Json;

namespace Orbit.Contracts;

/// <summary>Model-visible tool definition snapshot (for Trajectory Schema tab / session JSONL).</summary>
public sealed class ToolSchemaDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    /// <summary>JSON Schema for parameters (object), when available.</summary>
    public JsonElement? Parameters { get; init; }
    /// <summary>True when wrapped in ApprovalRequiredAIFunction (HITL).</summary>
    public bool RequiresApproval { get; init; }
    /// <summary>domain | harness | mcp</summary>
    public string Source { get; init; } = "domain";
}

