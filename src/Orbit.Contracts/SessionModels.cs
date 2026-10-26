using System.Text.Json;

namespace Orbit.Contracts;

/// <summary>Pi-style session tree header + entries (JSONL on disk).</summary>
public sealed class SessionHeader
{
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? CurrentEntryId { get; init; }
    /// <summary>Set when the session is archived (hidden from default History).</summary>
    public DateTimeOffset? ArchivedAt { get; init; }
    /// <summary>Short archive Summary generated at archive time.</summary>
    public string? Summary { get; init; }
    /// <summary>Cached entry count for History list without reading JSONL.</summary>
    public int EntryCount { get; init; }
    /// <summary>Agent id whose tool_catalog was already written (skip per-turn re-append).</summary>
    public string? ToolCatalogAgentId { get; init; }

    /// <summary>Active workspace id snapshotted at session create (immutable).</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>Active workspace path snapshotted at session create (immutable).</summary>
    public string? WorkspacePath { get; init; }
}

public sealed class SessionEntry
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    /// <summary>
    /// Discriminator — must be a <see cref="SessionEventTypes"/> value.
    /// Payload shapes are enforced at write time by <see cref="SessionEvents.Validate"/>.
    /// </summary>
    public required string Type { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public JsonElement? Payload { get; init; }
}

public sealed class SessionSummary
{
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int EntryCount { get; init; }
    public bool IsArchived { get; init; }
    public string? Summary { get; init; }
    public string? WorkspacePath { get; init; }
}

public sealed class SessionDetail
{
    public required SessionHeader Header { get; init; }
    public required IReadOnlyList<SessionEntry> Entries { get; init; }
}

/// <summary>
/// Incremental session read: <c>GET /sessions/{id}?after={entryId}</c>.
/// When <see cref="FullSnapshot"/> is true, <see cref="Entries"/> is the full list (replace client state).
/// Otherwise <see cref="Entries"/> are strictly after <c>after</c> (append / upsert by id).
/// </summary>
public sealed class SessionSyncResponse
{
    public required SessionHeader Header { get; init; }
    public required IReadOnlyList<SessionEntry> Entries { get; init; }
    public bool FullSnapshot { get; init; }
}

public sealed class CreateSessionRequest
{
    public required string AgentId { get; init; }
    public string? Title { get; init; }
    public string? WorkspaceId { get; init; }
    /// <summary>Resolved path snapshotted by Runtime (Shell usually omits).</summary>
    public string? WorkspacePath { get; init; }
}

public sealed class RenameSessionRequest
{
    public string? Title { get; init; }
}

/// <summary>
/// Wire DTO for append. Prefer <see cref="SessionEvents"/> factories; Runtime validates via <see cref="SessionEvents.Validate"/>.
/// </summary>
public sealed class AppendEntryRequest
{
    public string? ParentId { get; init; }
    public required string Type { get; init; }
    public JsonElement? Payload { get; init; }
}

public sealed class RunSessionRequest
{
    public required string Message { get; init; }
}

public sealed class DecideApprovalRequest
{
    /// <summary>approve | reject | retry</summary>
    public required string Decision { get; init; }
    public string? Note { get; init; }

    /// <summary>
    /// For <c>ask_user_choice</c> / confirm cards: the option label (or "approved") injected into tool args on resume.
    /// </summary>
    public string? Selected { get; init; }
}
