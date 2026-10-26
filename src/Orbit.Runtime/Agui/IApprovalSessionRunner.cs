using Orbit.Contracts;

namespace Orbit.Runtime.Agui;

/// <summary>
/// Approval channel needs: busy probe, resume interrupt, cancel drain.
/// Extracted so <see cref="Approvals.ApprovalService"/> can be tested without Playwright/AG-UI.
/// </summary>
public interface IApprovalSessionRunner
{
    bool IsSessionBusy(string sessionId);

    Task<SessionDetail> ResumeApprovalAsync(
        string sessionId,
        string entryId,
        bool approved,
        CancellationToken cancellationToken,
        string? selected = null);

    Task<bool> CancelAndDrainAsync(
        string sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
