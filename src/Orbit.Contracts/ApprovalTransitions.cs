namespace Orbit.Contracts;

/// <summary>Approval card status values persisted in session JSONL.</summary>
public static class ApprovalStatuses
{
    public const string Pending = "pending";
    public const string Failed = "failed";
    public const string Approving = "approving";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Queued = "queued";
}

/// <summary>
/// Pure approval-card invariants (no IO). Used by SessionStore / Shell / tests.
/// </summary>
public static class ApprovalTransitions
{
    public static bool CanApprove(ApprovalStatus status) =>
        status is ApprovalStatus.Pending or ApprovalStatus.Failed;

    public static bool CanReject(ApprovalStatus status) => CanApprove(status);

    /// <summary>
    /// Promote a queued card only when nothing is pending, failed, or approving.
    /// <paramref name="hasActiveCard"/> is that check.
    /// </summary>
    public static bool CanPromoteQueue(bool hasActiveCard) => !hasActiveCard;

    public static bool CanApproveOrRetry(string? status)
    {
        var s = Normalize(status);
        // Approving: allow retry after a failed resume that left the card mid-flight
        // (belt-and-suspenders; ApprovalService also rolls busy back to Failed).
        return s is ApprovalStatuses.Pending or ApprovalStatuses.Failed or ApprovalStatuses.Approving;
    }

    public static bool CanReject(string? status) => CanApproveOrRetry(status);

    /// <summary>Active card still needs user or apply attention.</summary>
    public static bool IsActiveCard(string? status)
    {
        var s = Normalize(status);
        return s is ApprovalStatuses.Pending or ApprovalStatuses.Failed or ApprovalStatuses.Approving;
    }

    public static bool CanPromoteQueued(IEnumerable<string?> statuses) =>
        !statuses.Any(IsActiveCard);

    public static string NextStatusAfterDecision(string decision)
    {
        var d = decision.Trim().ToLowerInvariant();
        return d switch
        {
            "reject" => ApprovalStatuses.Rejected,
            "approve" or "retry" => ApprovalStatuses.Approving,
            _ => throw new ArgumentException(
                $"Unsupported decision '{decision}'. Use approve, reject, or retry.",
                nameof(decision))
        };
    }

    public static string Normalize(string? status) =>
        string.IsNullOrWhiteSpace(status) ? ApprovalStatuses.Pending : status.Trim().ToLowerInvariant();
}
