using System.Collections.Concurrent;
using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

/// <summary>
/// Concurrent decide against the same approval entry: store gate serializes BeginDecision;
/// second call must not leave corrupt JSONL (status machine + lock).
/// </summary>
public class ApprovalConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orbit-appr-" + Guid.NewGuid().ToString("N"));
    private readonly JsonlSessionStore _store;

    public ApprovalConcurrencyTests()
    {
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
    public async Task Parallel_BeginApprovalDecision_leaves_single_approving_card()
    {
        var created = _store.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        var sid = created.Header.SessionId;
        var pending = _store.Append(sid, SessionEvents.Approval(new
        {
            status = ApprovalStatuses.Pending,
            source = ApprovalSources.Tool,
            toolName = "file_access_write",
            text = "write?"
        }));
        var entryId = pending.Entries.Last(e => e.Type == SessionEventTypes.Approval).Id;

        var errors = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try
            {
                _store.BeginApprovalDecision(sid, entryId, new DecideApprovalRequest { Decision = "approve" });
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(true);

        var detail = _store.Get(sid);
        Assert.NotNull(detail);
        var card = detail!.Entries.First(e => e.Id == entryId);
        var status = ((JsonElement)card.Payload!).GetProperty("status").GetString();
        Assert.Equal(ApprovalStatuses.Approving, status);

        // At least one attempt must succeed; extras may fail (already approving / race) — never corrupt.
        Assert.True(errors.Count < 8, "every parallel decide failed — unexpected");
        foreach (var ex in errors)
        {
            Assert.True(
                ex is InvalidOperationException or ArgumentException,
                $"unexpected exception type: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public void BeginDecision_second_approve_while_approved_throws()
    {
        var header = new SessionHeader
        {
            SessionId = "dddddddddddddddddddddddddddddddd",
            AgentId = "coder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var approval = new SessionEntry
        {
            Id = "e1",
            Type = SessionEventTypes.Approval,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                status = ApprovalStatuses.Approved,
                source = ApprovalSources.Tool,
                toolName = "x"
            })
        };

        var detail = new SessionDetail { Header = header, Entries = [approval] };
        Assert.Throws<InvalidOperationException>(() =>
            ApprovalWorkflow.BeginDecision(detail, "e1", new DecideApprovalRequest { Decision = "approve" }));
    }
}
