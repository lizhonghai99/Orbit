using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Contracts;

namespace Orbit.Runtime.Sessions;

/// <summary>
/// Approval card state machine (pure transform on <see cref="SessionDetail"/>).
/// Payload fields beyond the status machine are treated as opaque JSON and preserved.
/// Persistence stays in <see cref="JsonlSessionStore"/>.
/// </summary>
public static class ApprovalWorkflow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static SessionDetail BeginDecision(
        SessionDetail existing,
        string entryId,
        DecideApprovalRequest request)
    {
        var decision = request.Decision.Trim().ToLowerInvariant();
        if (decision is not ("approve" or "reject" or "retry"))
        {
            throw new ArgumentException("Decision must be 'approve', 'reject', or 'retry'.");
        }

        var index = existing.Entries.ToList().FindIndex(e => e.Id == entryId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Entry '{entryId}' not found.");
        }

        var target = existing.Entries[index];
        if (!string.Equals(target.Type, "approval", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only approval entries can be decided.");
        }

        var status = ApprovalTransitions.Normalize(SessionPayloadJson.ReadString(target, "status"));
        if (decision is "approve" or "retry")
        {
            if (!ApprovalTransitions.CanApproveOrRetry(status))
            {
                throw new InvalidOperationException($"Approval is '{status}', cannot approve/retry.");
            }
        }
        else if (!ApprovalTransitions.CanReject(status))
        {
            throw new InvalidOperationException($"Approval is already '{status}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var nextStatus = ApprovalTransitions.NextStatusAfterDecision(decision);
        var updatedPayload = MutatePayload(target, obj =>
        {
            obj["status"] = nextStatus;
            obj["decidedAt"] = now;
            obj["note"] = request.Note;
            obj["requiresApproval"] = nextStatus is ApprovalStatuses.Approving
                or ApprovalStatuses.Failed
                or ApprovalStatuses.Pending;
        });

        var entries = existing.Entries.ToList();
        entries[index] = new SessionEntry
        {
            Id = target.Id,
            ParentId = target.ParentId,
            Type = target.Type,
            Timestamp = target.Timestamp,
            Payload = updatedPayload
        };

        var decisionEntry = new SessionEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentId = target.Id,
            Type = "meta",
            Timestamp = now,
            Payload = JsonSerializer.SerializeToElement(new
            {
                kind = SessionMetaKinds.ApprovalDecision,
                entryId,
                decision = decision == "retry" ? "approve" : decision,
                suggestedAction = SessionPayloadJson.ReadString(target, "suggestedAction"),
                message = decision == "reject"
                    ? "Rejected; no page actions ran."
                    : "Running…"
            }, JsonOptions)
        };
        entries.Add(decisionEntry);

        return new SessionDetail
        {
            Header = StampHeader(existing.Header, now, decisionEntry.Id, entries.Count),
            Entries = entries
        };
    }

    public static SessionDetail Complete(
        SessionDetail existing,
        string entryId,
        bool success,
        string? failMessage = null)
    {
        var index = existing.Entries.ToList().FindIndex(e => e.Id == entryId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Entry '{entryId}' not found.");
        }

        var target = existing.Entries[index];
        var now = DateTimeOffset.UtcNow;
        var updatedPayload = MutatePayload(target, obj =>
        {
            obj["status"] = success ? ApprovalStatuses.Approved : ApprovalStatuses.Failed;
            obj["decidedAt"] = now;
            obj["requiresApproval"] = !success;
            obj["failMessage"] = success ? null : failMessage;
        });

        var entries = existing.Entries.ToList();
        entries[index] = new SessionEntry
        {
            Id = target.Id,
            ParentId = target.ParentId,
            Type = target.Type,
            Timestamp = target.Timestamp,
            Payload = updatedPayload
        };

        var meta = new SessionEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentId = target.Id,
            Type = "meta",
            Timestamp = now,
            Payload = JsonSerializer.SerializeToElement(new
            {
                kind = success ? SessionMetaKinds.ApprovalApplied : SessionMetaKinds.ApprovalFailed,
                entryId,
                ok = success,
                message = success ? "Approved; execution attempted." : (failMessage ?? "Failed; you can retry.")
            }, JsonOptions)
        };
        entries.Add(meta);

        return new SessionDetail
        {
            Header = StampHeader(existing.Header, now, meta.Id, entries.Count),
            Entries = entries
        };
    }

    public static SessionDetail PromoteNextQueued(SessionDetail existing)
    {
        var entries = existing.Entries.ToList();
        if (!ApprovalTransitions.CanPromoteQueued(entries.Select(ReadStatus)))
        {
            return existing;
        }

        var index = entries.FindIndex(e => ReadStatus(e) == ApprovalStatuses.Queued);
        if (index < 0)
        {
            return existing;
        }

        var target = entries[index];
        var now = DateTimeOffset.UtcNow;
        var remaining = entries.Count(e => ReadStatus(e) == ApprovalStatuses.Queued) - 1;
        var updatedPayload = MutatePayload(target, obj =>
        {
            obj["status"] = ApprovalStatuses.Pending;
            obj["requiresApproval"] = true;
            obj["promotedAt"] = now;
            obj["remainingQueued"] = Math.Max(0, remaining);
        });

        entries[index] = new SessionEntry
        {
            Id = target.Id,
            ParentId = target.ParentId,
            Type = target.Type,
            Timestamp = target.Timestamp,
            Payload = updatedPayload
        };

        var promo = new SessionEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ParentId = target.Id,
            Type = "meta",
            Timestamp = now,
            Payload = JsonSerializer.SerializeToElement(new
            {
                kind = SessionMetaKinds.ApprovalPromoted,
                entryId = target.Id,
                remainingQueued = Math.Max(0, remaining),
                hint = remaining > 0
                    ? $"Next approval is ready; {remaining} still queued."
                    : "This was the last queued approval."
            }, JsonOptions)
        };
        entries.Add(promo);

        return new SessionDetail
        {
            Header = StampHeader(existing.Header, now, promo.Id, entries.Count),
            Entries = entries
        };
    }

    private static SessionHeader StampHeader(
        SessionHeader source,
        DateTimeOffset updatedAt,
        string currentEntryId,
        int entryCount) =>
        new()
        {
            SessionId = source.SessionId,
            AgentId = source.AgentId,
            Title = source.Title,
            CreatedAt = source.CreatedAt,
            UpdatedAt = updatedAt,
            CurrentEntryId = currentEntryId,
            ArchivedAt = source.ArchivedAt,
            Summary = source.Summary,
            EntryCount = entryCount,
            ToolCatalogAgentId = source.ToolCatalogAgentId,
            // Workspace snapshot is immutable for the session lifetime — must survive approvals.
            WorkspaceId = source.WorkspaceId,
            WorkspacePath = source.WorkspacePath
        };

    private static string? ReadStatus(SessionEntry entry)
    {
        if (!string.Equals(entry.Type, "approval", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return ApprovalTransitions.Normalize(SessionPayloadJson.ReadString(entry, "status"));
    }


    private static JsonElement MutatePayload(SessionEntry target, Action<JsonObject> mutate)
    {
        var raw = target.Payload is JsonElement el && el.ValueKind != JsonValueKind.Undefined
            ? el.GetRawText()
            : "{}";
        var obj = JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
        mutate(obj);
        return JsonSerializer.SerializeToElement(obj, JsonOptions);
    }
}
