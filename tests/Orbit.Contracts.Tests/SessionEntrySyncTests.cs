using System.Text.Json;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class SessionEntrySyncTests
{
    [Fact]
    public void MergeEntries_appends_and_upserts_by_id()
    {
        var a = Entry("1", SessionEventTypes.User, "a");
        var b = Entry("2", SessionEventTypes.Assistant, "b");
        var b2 = Entry("2", SessionEventTypes.Assistant, "b-updated");
        var c = Entry("3", SessionEventTypes.Assistant, "c");

        var merged = SessionEntrySync.MergeEntries([a, b], [b2, c]);
        Assert.Equal(3, merged.Count);
        Assert.Equal("b-updated", TextOf(merged[1]));
        Assert.Equal("3", merged[2].Id);
    }

    [Fact]
    public void DeltaNeedsFullReload_on_approval_meta()
    {
        var meta = new SessionEntry
        {
            Id = "m1",
            Type = SessionEventTypes.Meta,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                kind = SessionMetaKinds.ApprovalDecision
            })
        };
        Assert.True(SessionEntrySync.DeltaNeedsFullReload([meta]));
        Assert.True(SessionEntrySync.RequiresFullFetch(new SessionSyncResponse
        {
            Header = new SessionHeader
            {
                SessionId = "s",
                AgentId = "mail",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            Entries = [meta],
            FullSnapshot = false
        }));
    }

    [Fact]
    public void ApplySync_full_snapshot_replaces()
    {
        var local = new SessionDetail
        {
            Header = Hdr(),
            Entries = [Entry("1", SessionEventTypes.User, "old")]
        };
        var sync = new SessionSyncResponse
        {
            Header = Hdr(),
            Entries = [Entry("9", SessionEventTypes.User, "new")],
            FullSnapshot = true
        };
        var next = SessionEntrySync.ApplySync(local, sync);
        Assert.Single(next.Entries);
        Assert.Equal("9", next.Entries[0].Id);
    }

    private static SessionHeader Hdr() => new()
    {
        SessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        AgentId = "coder",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static SessionEntry Entry(string id, string type, string text) => new()
    {
        Id = id,
        Type = type,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(new { text })
    };

    private static string TextOf(SessionEntry e) =>
        e.Payload is JsonElement el && el.TryGetProperty("text", out var t)
            ? t.GetString() ?? ""
            : "";
}
