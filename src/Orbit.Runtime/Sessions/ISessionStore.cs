using Orbit.Contracts;

namespace Orbit.Runtime.Sessions;

/// <summary>
/// Session persistence seam — JSONL today, SQLite/memory later without changing callers.
/// </summary>
public interface ISessionStore
{
    IReadOnlyList<SessionSummary> List(bool includeArchived = false);
    SessionDetail? Get(string sessionId);

    /// <summary>
    /// Incremental read. Omitting <paramref name="afterEntryId"/> or unknown id → full snapshot.
    /// </summary>
    SessionSyncResponse? GetSync(string sessionId, string? afterEntryId);
    SessionDetail Create(CreateSessionRequest request);
    int RecoverInterruptedRuns(string? agentIdFilter, string reason);
    SessionDetail Append(string sessionId, AppendEntryRequest request);
    void MarkToolCatalog(string sessionId, string agentId);
    SessionDetail Rename(string sessionId, string title);
    bool Delete(string sessionId);
    SessionDetail Archive(string sessionId, string summary);
    SessionDetail BeginApprovalDecision(string sessionId, string entryId, DecideApprovalRequest request);
    SessionDetail CompleteApproval(string sessionId, string entryId, bool success, string? failMessage = null);
    SessionDetail PromoteNextQueuedApproval(string sessionId);
}
