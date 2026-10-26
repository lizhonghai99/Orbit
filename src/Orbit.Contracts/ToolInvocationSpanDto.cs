using System.Text.Json;

namespace Orbit.Contracts;

/// <summary>
/// Agent-side function invocation span (precise wall clock + schema), exposed via <c>GET /telemetry/invocations</c>.
/// </summary>
public sealed class ToolInvocationSpanDto
{
    public string? CallId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public long DurationMs { get; init; }
    /// <summary>Optional JSON schema — prefer session tool_catalog; omit on merge when possible.</summary>
    public JsonElement? ParametersSchema { get; init; }
    public JsonElement? Arguments { get; init; }
    public string? ResultPreview { get; init; }
    public string? Error { get; init; }
    public int? Iteration { get; init; }
    /// <summary>Orbit run nonce (header <c>X-Orbit-Run-Id</c>) so Runtime can filter concurrent sessions.</summary>
    public string? RunId { get; init; }
    public string? SessionId { get; init; }
}
