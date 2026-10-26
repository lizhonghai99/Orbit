namespace Orbit.Contracts;

/// <summary>
/// Lifecycle of an approval card.
/// Disk and wire payloads still store the lower-case name (<see cref="ApprovalStatuses"/>).
/// <see cref="SessionEvents.Validate"/> remains the schema gate for approval events.
/// </summary>
public enum ApprovalStatus
{
    Pending,
    Failed,
    Approving,
    Approved,
    Rejected,
    Queued
}
