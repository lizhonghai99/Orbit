using Orbit.Contracts;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

public class SessionWorkspaceStoreTests : IDisposable
{
    private readonly string _root;
    private readonly JsonlSessionStore _store;

    public SessionWorkspaceStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-sess-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "sessions"));
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
    public void Create_persists_workspace_snapshot()
    {
        var created = _store.Create(new CreateSessionRequest
        {
            AgentId = "coder",
            Title = "ws chat",
            WorkspaceId = "wid1",
            WorkspacePath = @"D:\LearnCode\Demo"
        });

        Assert.Equal("wid1", created.Header.WorkspaceId);
        Assert.Equal(@"D:\LearnCode\Demo", created.Header.WorkspacePath);

        var loaded = _store.Get(created.Header.SessionId);
        Assert.NotNull(loaded);
        Assert.Equal("wid1", loaded!.Header.WorkspaceId);
        Assert.Equal(@"D:\LearnCode\Demo", loaded.Header.WorkspacePath);

        var list = _store.List();
        var summary = Assert.Single(list);
        Assert.Equal(@"D:\LearnCode\Demo", summary.WorkspacePath);
    }

    [Fact]
    public void Create_without_workspace_keeps_null()
    {
        var created = _store.Create(new CreateSessionRequest
        {
            AgentId = "mail",
            Title = "mail chat"
        });

        Assert.Null(created.Header.WorkspaceId);
        Assert.Null(created.Header.WorkspacePath);
    }
}
