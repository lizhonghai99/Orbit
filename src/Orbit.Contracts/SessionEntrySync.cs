using System.Text.Json;

namespace Orbit.Contracts;

/// <summary>
/// Pure merge / reload rules for incremental session sync (Shell + tests).
/// </summary>
public static class SessionEntrySync
{
    public static bool DeltaNeedsFullReload(IReadOnlyList<SessionEntry> delta)
    {
        foreach (var entry in delta)
        {
            if (string.Equals(entry.Type, SessionEventTypes.Approval, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(entry.Type, SessionEventTypes.Meta, StringComparison.OrdinalIgnoreCase)
                || entry.Payload is not JsonElement payload
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("kind", out var kind))
            {
                continue;
            }

            var k = kind.GetString();
            if (string.Equals(k, SessionMetaKinds.ApprovalDecision, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, SessionMetaKinds.ApprovalApplied, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, SessionMetaKinds.ApprovalFailed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, SessionMetaKinds.ApprovalPromoted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, SessionMetaKinds.ApprovalQueue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<SessionEntry> MergeEntries(
        IReadOnlyList<SessionEntry> existing,
        IReadOnlyList<SessionEntry> delta)
    {
        if (delta.Count == 0)
        {
            return existing;
        }

        var list = existing.ToList();
        var indexById = new Dictionary<string, int>(list.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            indexById[list[i].Id] = i;
        }

        foreach (var entry in delta)
        {
            if (indexById.TryGetValue(entry.Id, out var idx))
            {
                list[idx] = entry;
            }
            else
            {
                indexById[entry.Id] = list.Count;
                list.Add(entry);
            }
        }

        return list;
    }

    /// <summary>
    /// True when the client must re-GET the full session (approval rewrite) rather than merge a delta.
    /// </summary>
    public static bool RequiresFullFetch(SessionSyncResponse sync) =>
        !sync.FullSnapshot && DeltaNeedsFullReload(sync.Entries);

    public static SessionDetail ApplySync(SessionDetail? local, SessionSyncResponse sync)
    {
        if (sync.FullSnapshot || local is null)
        {
            return new SessionDetail { Header = sync.Header, Entries = sync.Entries };
        }

        return new SessionDetail
        {
            Header = sync.Header,
            Entries = MergeEntries(local.Entries, sync.Entries)
        };
    }
}
