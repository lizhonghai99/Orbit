using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

public class McpConfigServiceTests : IDisposable
{
    private readonly string _root;
    private readonly RepoPaths _paths;
    private readonly AgentRegistry _registry;
    private readonly RecordingSessionStore _sessions;
    private readonly McpConfigService _sut;

    public McpConfigServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "sessions"));
        File.WriteAllText(
            Path.Combine(_root, "agents", "research.agent.json"),
            """
            {
              "id": "research",
              "displayName": "x",
              "project": "src/Orbit.Agents.Research/Orbit.Agents.Research.csproj",
              "protocol": "ag-ui"
            }
            """);

        _paths = new RepoPaths { RepoRoot = _root };
        _registry = new AgentRegistry(_paths);
        _sessions = new RecordingSessionStore();
        _sut = new McpConfigService(
            _paths,
            _registry,
            _sessions,
            new NoopToolSchemaCache(),
            NullLogger<McpConfigService>.Instance);
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
            // best-effort temp cleanup
        }
    }

    [Fact]
    public void Load_missing_file_returns_empty()
    {
        var file = _sut.Load("research");
        Assert.Empty(file.Servers!);
    }

    [Fact]
    public void Put_writes_atomically_and_requires_restart()
    {
        var result = _sut.Put("research", new McpConfigFile
        {
            Servers =
            [
                new McpServerConfig
                {
                    Id = "tavily",
                    Transport = "http",
                    Url = "https://mcp.tavily.com/mcp",
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer ${TAVILY_API_KEY}"
                    },
                    Trusted = false,
                    Enabled = true
                }
            ]
        });

        Assert.True(result.Saved);
        Assert.True(result.RequiresRestart);
        Assert.Equal(1, result.Servers);
        Assert.True(File.Exists(Path.Combine(_root, "data", "mcp", "research.json")));

        var loaded = _sut.Load("research");
        var servers = loaded.Servers!;
        Assert.Single(servers);
        Assert.Equal("tavily", servers[0].Id);
        Assert.False(servers[0].Trusted);
        Assert.Equal(1, loaded.Revision);
    }

    [Fact]
    public void Put_rejects_stale_revision()
    {
        _sut.Put("research", OneHttpServer(trusted: false)); // revision 0 → 1
        var stale = OneHttpServer(trusted: true); // Revision defaults to 0
        var ex = Assert.Throws<OrbitApiException>(() => _sut.Put("research", stale));
        Assert.Equal(OrbitErrorCodes.Conflict, ex.Code);
        Assert.Contains("mcp_config_stale", ex.Message, StringComparison.OrdinalIgnoreCase);

        var fresh = _sut.Load("research");
        Assert.Equal(1, fresh.Revision);
        var ok = _sut.Put("research", new McpConfigFile
        {
            Revision = fresh.Revision,
            Servers =
            [
                new McpServerConfig
                {
                    Id = "tavily",
                    Transport = "http",
                    Url = "https://mcp.tavily.com/mcp",
                    Trusted = true,
                    Enabled = true
                }
            ]
        });
        Assert.True(ok.Saved);
        Assert.Equal(2, _sut.Load("research").Revision);
    }

    [Fact]
    public void Put_rejects_missing_servers_field()
    {
        var ex = Assert.Throws<ArgumentException>(() => _sut.Put("research", new McpConfigFile()));
        Assert.Contains("mcp_servers_required", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_sut.Load("research").Servers!);
    }

    [Fact]
    public void Put_explicit_empty_servers_clears_with_warning()
    {
        _sut.Put("research", OneHttpServer(trusted: false));
        var result = _sut.Put("research", new McpConfigFile
        {
            Revision = 1,
            Servers = []
        });
        Assert.True(result.Saved);
        Assert.Equal(0, result.Servers);
        Assert.Contains(result.Warnings, w => w.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_sut.Load("research").Servers!);
    }

    [Fact]
    public void Put_unknown_agent_throws()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            _sut.Put("nope", new McpConfigFile { Servers = [] }));
    }

    [Fact]
    public void SetTrust_updates_file_and_audits_jsonl_plus_latest_session()
    {
        _sut.Put("research", OneHttpServer(trusted: false));
        _sessions.Summaries.Add(new SessionSummary
        {
            SessionId = "s1",
            AgentId = "research",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        _sessions.Summaries.Add(new SessionSummary
        {
            SessionId = "s2",
            AgentId = "research",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });

        var result = _sut.SetTrust("research", "tavily", trusted: true);
        Assert.True(result.RequiresRestart);
        Assert.True(_sut.Load("research").Servers![0].Trusted);

        // Only the newest active session gets a breadcrumb (not every session).
        Assert.Single(_sessions.Appends);
        Assert.Equal("s2", _sessions.Appends[0].SessionId);
        var p = _sessions.Appends[0].Request.Payload!.Value;
        Assert.Equal(SessionMetaKinds.McpChanged, p.GetProperty("kind").GetString());
        Assert.Equal("trust", p.GetProperty("action").GetString());

        var auditPath = Path.Combine(_root, "data", "audit", "mcp.jsonl");
        Assert.True(File.Exists(auditPath));
        Assert.Contains("\"action\":\"trust\"", File.ReadAllText(auditPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_removes_server_and_audits()
    {
        _sut.Put("research", OneHttpServer(trusted: true));
        _sessions.Summaries.Add(new SessionSummary
        {
            SessionId = "s2",
            AgentId = "research",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var result = _sut.Delete("research", "tavily");
        Assert.True(result.RequiresRestart);
        Assert.Empty(_sut.Load("research").Servers!);
        Assert.Contains(_sessions.Appends, a =>
            a.Request.Payload is { } p
            && p.GetProperty("action").GetString() == "delete");
    }

    [Fact]
    public void Delete_missing_server_throws()
    {
        _sut.Put("research", new McpConfigFile { Servers = [] });
        Assert.Throws<KeyNotFoundException>(() => _sut.Delete("research", "missing"));
    }

    [Fact]
    public void Audit_skips_when_no_active_session()
    {
        _sut.Put("research", OneHttpServer(trusted: true));
        Assert.Empty(_sessions.Appends);
    }

    private static McpConfigFile OneHttpServer(bool trusted) => new()
    {
        Servers =
        [
            new McpServerConfig
            {
                Id = "tavily",
                Transport = "http",
                Url = "https://mcp.tavily.com/mcp",
                Trusted = trusted,
                Enabled = true
            }
        ]
    };

    private sealed class RecordingSessionStore : ISessionStore
    {
        public List<SessionSummary> Summaries { get; } = [];
        public List<(string SessionId, AppendEntryRequest Request)> Appends { get; } = [];

        public IReadOnlyList<SessionSummary> List(bool includeArchived = false) =>
            Summaries.Where(s => includeArchived || !s.IsArchived).ToList();

        public SessionDetail Append(string sessionId, AppendEntryRequest request)
        {
            Appends.Add((sessionId, request));
            return new SessionDetail
            {
                Header = new SessionHeader
                {
                    SessionId = sessionId,
                    AgentId = "research",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                Entries = []
            };
        }

        public SessionDetail? Get(string sessionId) => throw new NotSupportedException();
        public SessionSyncResponse? GetSync(string sessionId, string? afterEntryId) => throw new NotSupportedException();
        public SessionDetail Create(CreateSessionRequest request) => throw new NotSupportedException();
        public int RecoverInterruptedRuns(string? agentIdFilter, string reason) => 0;
        public void MarkToolCatalog(string sessionId, string agentId) { }
        public SessionDetail Rename(string sessionId, string title) => throw new NotSupportedException();
        public bool Delete(string sessionId) => false;
        public SessionDetail Archive(string sessionId, string summary) => throw new NotSupportedException();
        public SessionDetail BeginApprovalDecision(string sessionId, string entryId, DecideApprovalRequest request) =>
            throw new NotSupportedException();
        public SessionDetail CompleteApproval(string sessionId, string entryId, bool success, string? failMessage = null) =>
            throw new NotSupportedException();
        public SessionDetail PromoteNextQueuedApproval(string sessionId) => throw new NotSupportedException();
    }
}
