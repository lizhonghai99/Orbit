using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Agui;
using Orbit.Runtime.Sessions;

namespace Orbit.Runtime.Approvals;

/// <summary>
/// Host approval channel: resume tool-approval interrupts, or close legacy cards without
/// interpreting agent-domain payload fields.
/// </summary>
public sealed class ApprovalService(
    ISessionStore sessions,
    IApprovalSessionRunner agui,
    ILogger<ApprovalService> logger)
{
    public async Task<SessionDetail> DecideAsync(
        string sessionId,
        string entryId,
        DecideApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var decision = request.Decision.Trim().ToLowerInvariant();
        // Same allowlist for tool interrupts and legacy cards — unknown values must not default-approve.
        if (decision is not ("approve" or "reject" or "retry"))
        {
            throw new ArgumentException(
                $"Unsupported decision '{request.Decision}'. Use approve, reject, or retry.",
                nameof(request));
        }

        var before = sessions.Get(sessionId)
                     ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        var approvalEntry = before.Entries.FirstOrDefault(e => e.Id == entryId)
                            ?? throw new KeyNotFoundException($"Approval '{entryId}' not found.");

        if (IsToolApproval(approvalEntry))
        {
            return await DecideToolApprovalAsync(sessionId, entryId, decision, request.Selected, cancellationToken)
                .ConfigureAwait(false);
        }

        // Legacy JSONL cards (no tool interrupt): Runtime never applies side effects — record and close.
        _ = sessions.BeginApprovalDecision(sessionId, entryId, request);
        if (decision is "approve" or "retry")
        {
            sessions.CompleteApproval(sessionId, entryId, success: true);
        }

        return sessions.PromoteNextQueuedApproval(sessionId);
    }

    private async Task<SessionDetail> DecideToolApprovalAsync(
        string sessionId,
        string entryId,
        string decision,
        string? selected,
        CancellationToken cancellationToken)
    {
        var before = sessions.Get(sessionId)
                     ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        var entry = before.Entries.FirstOrDefault(e => e.Id == entryId)
                    ?? throw new KeyNotFoundException($"Approval '{entryId}' not found.");

        // Validate ask_user selection before mutating card status (keep pending on 400).
        if (decision is "approve"
            && TurnEventProjector.TryReadToolApproval(entry, out _, out _, out var toolName, out var args))
        {
            _ = OrbitAskTools.ApplySelectedOnApprove(toolName, ToMutableArgs(args), selected, approved: true);
        }

        // Do not flip the card to Approving while a turn still holds the run gate.
        if (decision is "approve" or "retry" && agui.IsSessionBusy(sessionId))
        {
            throw new OrbitApiException(
                OrbitErrorCodes.SessionBusy,
                "session_busy: wait for the current turn to finish before approving.");
        }

        sessions.BeginApprovalDecision(sessionId, entryId, new DecideApprovalRequest
        {
            Decision = decision,
            Selected = selected
        });

        if (decision is "reject")
        {
            try
            {
                await agui.ResumeApprovalAsync(sessionId, entryId, approved: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ShouldDrainAfterRejectResumeFailure(ex))
                {
                    // Leftover pending card rejected while another turn holds the gate.
                    // Card is already Rejected — do NOT CancelAndDrain (would kill the active turn).
                    logger.LogWarning(
                        ex,
                        "Reject resume hit session_busy for {SessionId}/{EntryId}; leaving active turn alone",
                        sessionId,
                        entryId);
                    throw;
                }

                // Card is already Rejected; force-cancel the waiting turn so the agent gate cannot hang.
                logger.LogError(
                    ex,
                    "Reject resume failed for {SessionId}/{EntryId}; cancelling session run",
                    sessionId,
                    entryId);
                try
                {
                    _ = await agui.CancelAndDrainAsync(sessionId, TimeSpan.FromSeconds(8), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception cancelEx)
                {
                    logger.LogWarning(
                        cancelEx,
                        "Cancel after reject-resume failure also failed for {SessionId}",
                        sessionId);
                }
            }

            return sessions.PromoteNextQueuedApproval(sessionId);
        }

        try
        {
            await agui.ResumeApprovalAsync(
                    sessionId,
                    entryId,
                    approved: true,
                    cancellationToken,
                    selected: selected)
                .ConfigureAwait(false);
            sessions.CompleteApproval(sessionId, entryId, success: true);
            return sessions.PromoteNextQueuedApproval(sessionId);
        }
        catch (OperationCanceledException ex)
        {
            // Distinguish HTTP/request abort from user Stop (run-abort CTS) for telemetry.
            var reason = cancellationToken.IsCancellationRequested ? "request_aborted" : "cancelled";
            logger.LogInformation(
                ex,
                "Approve resume cancelled ({CancelReason}) for {SessionId}/{EntryId}",
                reason,
                sessionId,
                entryId);
            sessions.CompleteApproval(sessionId, entryId, success: false, reason);
            return sessions.PromoteNextQueuedApproval(sessionId);
        }
        catch (Exception ex) when (IsRetryableApprovalFailure(ex))
        {
            // Card was already flipped to Approving — roll back to Failed so CanApproveOrRetry
            // still works (otherwise TOCTOU session_busy leaves the card permanently stuck).
            try
            {
                sessions.CompleteApproval(sessionId, entryId, success: false, ex.Message);
            }
            catch (Exception rollbackEx)
            {
                logger.LogWarning(
                    rollbackEx,
                    "Failed to roll back approving card {SessionId}/{EntryId}",
                    sessionId,
                    entryId);
            }

            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Approve resume failed for {SessionId}/{EntryId}", sessionId, entryId);
            return sessions.CompleteApproval(sessionId, entryId, success: false, ex.Message);
        }
    }

    /// <summary>Reject resume must not drain when the failure is only session_busy.</summary>
    internal static bool ShouldDrainAfterRejectResumeFailure(Exception ex) => !IsSessionBusyFailure(ex);

    private static bool IsSessionBusyFailure(Exception ex)
    {
        if (ex is OrbitApiException api && api.Is(OrbitErrorCodes.SessionBusy))
        {
            return true;
        }

        return ex is InvalidOperationException ioe
               && ioe.Message.StartsWith(OrbitErrorCodes.SessionBusy, StringComparison.Ordinal);
    }

    private static bool IsRetryableApprovalFailure(Exception ex)
    {
        if (ex is OrbitApiException api
            && (api.Is(OrbitErrorCodes.SessionBusy) || api.Is(OrbitErrorCodes.SelectionRequired)))
        {
            return true;
        }

        return ex is InvalidOperationException ioe
               && (ioe.Message.StartsWith(OrbitErrorCodes.SelectionRequired, StringComparison.Ordinal)
                   || ioe.Message.StartsWith(OrbitErrorCodes.SessionBusy, StringComparison.Ordinal));
    }

    private static Dictionary<string, object?>? ToMutableArgs(IDictionary<string, object?>? args)
    {
        if (args is null)
        {
            return null;
        }

        return args as Dictionary<string, object?>
               ?? new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsToolApproval(SessionEntry entry)
    {
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return ApprovalSources.IsToolInterrupt(SessionPayloadJson.ReadString(payload, "source"));
    }
}
