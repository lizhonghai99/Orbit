using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

public class JsonlSessionStoreLifecycleTests : IDisposable
{
    private readonly string _root;
    private readonly JsonlSessionStore _store;

    public JsonlSessionStoreLifecycleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-jsonl-life-" + Guid.NewGuid().ToString("N"));
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
    public void Create_append_list_delete_roundtrip()
    {
        var created = _store.Create(new CreateSessionRequest
        {
            AgentId = "coder",
            Title = "t1"
        });
        Assert.True(SessionIdRules.IsValid(created.Header.SessionId));
        Assert.Equal("coder", created.Header.AgentId);

        var appended = _store.Append(
            created.Header.SessionId,
            SessionEvents.User("hello"));
        Assert.Contains(appended.Entries, e => e.Type == "user");

        var listed = _store.List(includeArchived: false);
        Assert.Contains(listed, s => s.SessionId == created.Header.SessionId);

        Assert.True(_store.Delete(created.Header.SessionId));
        Assert.Null(_store.Get(created.Header.SessionId));
    }

    [Fact]
    public async Task Append_return_value_survives_concurrent_mutation_during_serialize()
    {
        var created = _store.Create(new CreateSessionRequest { AgentId = "coder", Title = "race" });
        var id = created.Header.SessionId;
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var ct = TestContext.Current.CancellationToken;

        Exception? serializeFault = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await started.Task.WaitAsync(ct);
            for (var i = 0; i < 200; i++)
            {
                ct.ThrowIfCancellationRequested();
                _store.Append(id, SessionEvents.Meta("noise", new { i }));
            }
        }, ct);

        for (var i = 0; i < 80; i++)
        {
            ct.ThrowIfCancellationRequested();
            var detail = _store.Append(id, SessionEvents.User($"u-{i}"));
            started.TrySetResult();
            try
            {
                _ = JsonSerializer.Serialize(detail, options);
            }
            catch (Exception ex)
            {
                serializeFault = ex;
                break;
            }
        }

        await writer;
        Assert.Null(serializeFault);
    }
}
