using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

public class CapabilityAuditTests : IDisposable
{
    private readonly string _root;
    private readonly RecordingSessionStore _sessions;
    private readonly CapabilityService _sut;

    public CapabilityAuditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-cap-audit-" + Guid.NewGuid().ToString("N"));
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

        var paths = new RepoPaths { RepoRoot = _root };
        var registry = new AgentRegistry(paths);
        _sessions = new RecordingSessionStore();
        var profileStore = new CapabilityProfileStore(paths, NullLogger<CapabilityProfileStore>.Instance);
        var httpFactory = new SimpleHttpClientFactory();
        _sut = new CapabilityService(
            paths,
            registry,
            profileStore,
            _sessions,
            httpFactory,
            new NoopToolSchemaCache(),
            NullLogger<CapabilityService>.Instance);
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
    public async Task Patch_writes_audit_jsonl_and_one_session_breadcrumb()
    {
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

        var result = await _sut.PatchAsync(
            "research",
            new CapabilityPatchRequest
            {
                DisabledSkills = ["skill-a"],
                DisabledTools = []
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AffectedSessions);
        Assert.Single(_sessions.Appends);
        Assert.Equal("s2", _sessions.Appends[0].SessionId);

        var auditPath = Path.Combine(_root, "data", "audit", "capabilities.jsonl");
        Assert.True(File.Exists(auditPath));
        Assert.Contains("\"action\":\"patch\"", File.ReadAllText(auditPath), StringComparison.Ordinal);
        Assert.Contains("skill-a", File.ReadAllText(auditPath), StringComparison.Ordinal);
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

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
