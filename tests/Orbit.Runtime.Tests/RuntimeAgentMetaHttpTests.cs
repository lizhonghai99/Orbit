using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

/// <summary>
/// Dual-token host: Shell unlocks all routes; Agent token only <c>POST …/agent-meta</c>.
/// </summary>
public class RuntimeAgentMetaHttpTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _shellToken = OrbitLocalAuth.CreateToken();
    private readonly string _agentToken = OrbitLocalAuth.CreateToken();
    private WebApplication? _app;
    private HttpClient? _client;
    private JsonlSessionStore? _store;

    public RuntimeAgentMetaHttpTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-agent-meta-" + Guid.NewGuid().ToString("N"));
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

        var shellToken = _shellToken;
        var agentToken = _agentToken;
        _app.Use(async (ctx, next) =>
        {
            if (OrbitLocalAuthGate.IsHealthExempt(ctx.Request.Method, ctx.Request.Path))
            {
                await next().ConfigureAwait(false);
                return;
            }

            ctx.Request.Headers.TryGetValue(OrbitLocalAuth.HeaderName, out var presented);
            var token = presented.ToString();
            var path = ctx.Request.Path.Value ?? "";
            var shellOk = OrbitLocalAuthGate.IsAuthorized(token, shellToken);
            var agentMetaOk = !shellOk
                              && OrbitLocalAuthGate.IsAgentMetaPath(ctx.Request.Method, path)
                              && OrbitLocalAuthGate.IsAuthorized(token, agentToken);
            if (!shellOk && !agentMetaOk)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next().ConfigureAwait(false);
        });

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
    public async Task Agent_token_can_post_research_stage()
    {
        var sid = _store!.Create(new CreateSessionRequest { AgentId = "research", Title = "t" })
            .Header.SessionId;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/sessions/{sid}/agent-meta")
        {
            Content = JsonContent.Create(SessionEvents.Meta(SessionMetaKinds.ResearchStage, new { stage = "Outline" }))
        };
        req.Headers.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, _agentToken);

        var response = await _client!.SendAsync(req, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = _store.Get(sid);
        Assert.Contains(
            detail!.Entries,
            e => e.Type == SessionEventTypes.Meta
                 && e.Payload is JsonElement p
                 && p.TryGetProperty("kind", out var k)
                 && k.GetString() == SessionMetaKinds.ResearchStage);
    }

    [Fact]
    public async Task Agent_token_cannot_post_public_entries()
    {
        var sid = _store!.Create(new CreateSessionRequest { AgentId = "research", Title = "t" })
            .Header.SessionId;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/sessions/{sid}/entries")
        {
            Content = JsonContent.Create(SessionEvents.User("nope"))
        };
        req.Headers.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, _agentToken);

        var response = await _client!.SendAsync(req, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Agent_meta_rejects_non_research_stage_kind()
    {
        var sid = _store!.Create(new CreateSessionRequest { AgentId = "research", Title = "t" })
            .Header.SessionId;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/sessions/{sid}/agent-meta")
        {
            Content = JsonContent.Create(SessionEvents.Meta(SessionMetaKinds.Usage, new { promptTokens = 1 }))
        };
        req.Headers.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, _agentToken);

        var response = await _client!.SendAsync(req, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
