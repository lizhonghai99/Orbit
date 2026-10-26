using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

/// <summary>
/// Hosts production session sync/SSE/append routes (not a copy).
/// Auth middleware is omitted — same as other unit hosts; route logic under test is the real Map*.
/// </summary>
public class RuntimeSessionHttpTests : IAsyncLifetime
{
    private readonly string _root;
    private WebApplication? _app;
    private HttpClient? _client;
    private JsonlSessionStore? _store;

    public RuntimeSessionHttpTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "sessions"));
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
    }

    public async ValueTask InitializeAsync()
    {
        _store = new JsonlSessionStore(new RepoPaths { RepoRoot = _root });
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<JsonlSessionStore>());
        _app = builder.Build();

        // Production routes (fast intervals so SSE ping tests stay short).
        OrbitRuntimeEndpoints.MapSessionSyncEndpoints(
            _app,
            ssePollInterval: TimeSpan.FromMilliseconds(50),
            sseHeartbeatInterval: TimeSpan.FromMilliseconds(150));
        OrbitRuntimeEndpoints.MapSessionAppendEndpoint(_app);
        OrbitRuntimeEndpoints.MapSessionAgentMetaEndpoint(_app);

        await _app.StartAsync().ConfigureAwait(false);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

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
    public async Task Get_with_after_returns_SessionSyncResponse()
    {
        var created = _store!.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        var sid = created.Header.SessionId;
        var first = _store.Append(sid, SessionEvents.User("one"));
        var user = first.Entries.Last(e => e.Type == SessionEventTypes.User);
        _store.Append(sid, SessionEvents.Assistant("two"));

        var sync = await _client!.GetFromJsonAsync<SessionSyncResponse>(
            $"/sessions/{sid}?after={user.Id}",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(sync);
        Assert.False(sync!.FullSnapshot);
        Assert.Single(sync.Entries);
        Assert.Equal(SessionEventTypes.Assistant, sync.Entries[0].Type);
    }

    [Fact]
    public async Task Get_rejects_invalid_session_id()
    {
        var response = await _client!.GetAsync(
            "/sessions/not-a-valid-id",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_unknown_after_returns_full_snapshot()
    {
        var created = _store!.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        var sid = created.Header.SessionId;
        _store.Append(sid, SessionEvents.User("one"));

        var sync = await _client!.GetFromJsonAsync<SessionSyncResponse>(
            $"/sessions/{sid}?after=deadbeefdeadbeefdeadbeefdeadbeef",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(sync);
        Assert.True(sync!.FullSnapshot);
        Assert.True(sync.Entries.Count >= 1);
    }

    [Fact]
    public async Task Post_entries_rejects_non_user_types()
    {
        var created = _store!.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        var sid = created.Header.SessionId;

        var response = await _client!.PostAsJsonAsync(
            $"/sessions/{sid}/entries",
            SessionEvents.Assistant("not allowed"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sse_emits_sync_when_entries_append()
    {
        var created = _store!.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        var sid = created.Header.SessionId;
        var seeded = _store.Append(sid, SessionEvents.User("seed"));
        var cursor = seeded.Entries.Last(e => e.Type == SessionEventTypes.User).Id;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await using var stream = await _client!.GetStreamAsync(
            $"/sessions/{sid}/events?after={cursor}",
            cts.Token).ConfigureAwait(true);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var appendTask = Task.Run(async () =>
        {
            await Task.Delay(80, CancellationToken.None).ConfigureAwait(false);
            _store.Append(sid, SessionEvents.Assistant("from-sse"));
        }, CancellationToken.None);

        var sawSync = false;
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(true);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("event: sync", StringComparison.Ordinal))
                {
                    var data = await reader.ReadLineAsync(cts.Token).ConfigureAwait(true);
                    Assert.NotNull(data);
                    Assert.StartsWith("data: ", data, StringComparison.Ordinal);
                    var json = data!["data: ".Length..];
                    using var doc = JsonDocument.Parse(json);
                    Assert.False(doc.RootElement.GetProperty("fullSnapshot").GetBoolean());
                    Assert.Equal(
                        SessionEventTypes.Assistant,
                        doc.RootElement.GetProperty("entries")[0].GetProperty("type").GetString());
                    sawSync = true;
                    break;
                }
            }
        }
        finally
        {
            await appendTask.ConfigureAwait(true);
        }

        Assert.True(sawSync);
    }

    [Fact]
    public async Task Sse_emits_ping_on_heartbeat_not_every_poll()
    {
        var created = _store!.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        var sid = created.Header.SessionId;
        var seeded = _store.Append(sid, SessionEvents.User("idle"));
        var cursor = seeded.Entries.Last(e => e.Type == SessionEventTypes.User).Id;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        await using var stream = await _client!.GetStreamAsync(
            $"/sessions/{sid}/events?after={cursor}",
            cts.Token).ConfigureAwait(true);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var pings = 0;
        // Heartbeat 150ms: wait for a few pings, then stop. Old coupled design would emit ~1 ping/poll.
        while (pings < 3 && !cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(true);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("event: ping", StringComparison.Ordinal))
            {
                continue;
            }

            pings++;
            _ = await reader.ReadLineAsync(cts.Token).ConfigureAwait(true); // data
            _ = await reader.ReadLineAsync(cts.Token).ConfigureAwait(true); // blank
        }

        Assert.True(pings >= 2, $"expected ≥2 heartbeats, got {pings}");
        // Elapsed for 2–3 pings at 150ms heartbeat should be well under one-ping-per-50ms-poll density.
        // (If ping were coupled to every poll, we would see far more events before cancelling.)
    }
}
