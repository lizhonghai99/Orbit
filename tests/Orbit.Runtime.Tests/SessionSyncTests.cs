using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

public class SessionSyncTests : IDisposable
{
    private readonly string _root;
    private readonly JsonlSessionStore _store;

    public SessionSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "sessions"));
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        _store = new JsonlSessionStore(new RepoPaths { RepoRoot = _root });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void GetSync_without_after_returns_full_snapshot()
    {
        var created = _store.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        _store.Append(created.Header.SessionId, SessionEvents.User("hi"));

        var sync = _store.GetSync(created.Header.SessionId, afterEntryId: null);
        Assert.NotNull(sync);
        Assert.True(sync!.FullSnapshot);
        Assert.True(sync.Entries.Count >= 2);
    }

    [Fact]
    public void GetSync_after_known_id_returns_only_delta()
    {
        var created = _store.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        var sid = created.Header.SessionId;
        var first = _store.Append(sid, SessionEvents.User("one"));
        var firstUser = first.Entries.Last(e => e.Type == SessionEventTypes.User);
        _store.Append(sid, SessionEvents.Assistant("two"));
        _store.Append(sid, SessionEvents.Assistant("three"));

        var sync = _store.GetSync(sid, firstUser.Id);
        Assert.NotNull(sync);
        Assert.False(sync!.FullSnapshot);
        Assert.Equal(2, sync.Entries.Count);
        Assert.All(sync.Entries, e => Assert.Equal(SessionEventTypes.Assistant, e.Type));
    }

    [Fact]
    public void GetSync_unknown_after_forces_full_snapshot()
    {
        var created = _store.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        _store.Append(created.Header.SessionId, SessionEvents.User("x"));

        var sync = _store.GetSync(created.Header.SessionId, afterEntryId: "deadbeefdeadbeefdeadbeefdeadbeef");
        Assert.NotNull(sync);
        Assert.True(sync!.FullSnapshot);
        Assert.NotEmpty(sync.Entries);
    }
}
