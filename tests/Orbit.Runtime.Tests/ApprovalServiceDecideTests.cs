using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Contracts;
using Orbit.Runtime.Agui;
using Orbit.Runtime.Approvals;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

/// <summary>
/// Wiring tests for reject/busy and cancel→card status (H1 / N1 from review).
/// </summary>
public class ApprovalServiceDecideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orbit-appr-svc-" + Guid.NewGuid().ToString("N"));
    private readonly JsonlSessionStore _store;

    public ApprovalServiceDecideTests()
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
    public void ShouldDrainAfterRejectResumeFailure_false_on_session_busy()
    {
        var busy = new OrbitApiException(OrbitErrorCodes.SessionBusy, "session_busy: Session already running.");
        Assert.False(ApprovalService.ShouldDrainAfterRejectResumeFailure(busy));
        Assert.True(ApprovalService.ShouldDrainAfterRejectResumeFailure(new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Reject_on_session_busy_does_not_cancel_and_drain()
    {
        var (sid, entryId) = SeedToolApproval();
        var fake = new FakeApprovalRunner
        {
            Busy = false,
            ResumeException = new OrbitApiException(
                OrbitErrorCodes.SessionBusy,
                "session_busy: Session already running.")
        };
        var sut = new ApprovalService(_store, fake, NullLogger<ApprovalService>.Instance);

        var ex = await Assert.ThrowsAsync<OrbitApiException>(() =>
            sut.DecideAsync(sid, entryId, new DecideApprovalRequest { Decision = "reject" }, CancellationToken.None));

        Assert.True(ex.Is(OrbitErrorCodes.SessionBusy));
        Assert.Equal(0, fake.CancelDrainCalls);
        Assert.Equal(1, fake.ResumeCalls);

        var card = _store.Get(sid)!.Entries.First(e => e.Id == entryId);
        Assert.Equal(ApprovalStatuses.Rejected, ((JsonElement)card.Payload!).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Approve_cancelled_resume_stamps_failed_not_approved()
    {
        var (sid, entryId) = SeedToolApproval();
        var fake = new FakeApprovalRunner
        {
            ResumeException = new OperationCanceledException("Approval resume was cancelled before the tool finished.")
        };
        var sut = new ApprovalService(_store, fake, NullLogger<ApprovalService>.Instance);

        var detail = await sut.DecideAsync(
            sid,
            entryId,
            new DecideApprovalRequest { Decision = "approve" },
            CancellationToken.None);

        var card = detail.Entries.First(e => e.Id == entryId);
        Assert.Equal(ApprovalStatuses.Failed, ((JsonElement)card.Payload!).GetProperty("status").GetString());
        Assert.Equal("cancelled", ((JsonElement)card.Payload!).GetProperty("failMessage").GetString());
        Assert.Equal(0, fake.CancelDrainCalls);
    }

    [Fact]
    public async Task Approve_request_abort_stamps_request_aborted()
    {
        var (sid, entryId) = SeedToolApproval();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var fake = new FakeApprovalRunner
        {
            ResumeException = new OperationCanceledException(cts.Token)
        };
        var sut = new ApprovalService(_store, fake, NullLogger<ApprovalService>.Instance);

        var detail = await sut.DecideAsync(
            sid,
            entryId,
            new DecideApprovalRequest { Decision = "approve" },
            cts.Token);

        var card = detail.Entries.First(e => e.Id == entryId);
        Assert.Equal(ApprovalStatuses.Failed, ((JsonElement)card.Payload!).GetProperty("status").GetString());
        Assert.Equal("request_aborted", ((JsonElement)card.Payload!).GetProperty("failMessage").GetString());
    }

    private (string SessionId, string EntryId) SeedToolApproval()
    {
        var created = _store.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" });
        var sid = created.Header.SessionId;
        var pending = _store.Append(sid, SessionEvents.Approval(new
        {
            status = ApprovalStatuses.Pending,
            source = ApprovalSources.Tool,
            requestId = "req-1",
            callId = "call-1",
            toolName = "file_access_write",
            arguments = new { path = "a.txt" },
            text = "write?"
        }));
        var entryId = pending.Entries.Last(e => e.Type == SessionEventTypes.Approval).Id;
        return (sid, entryId);
    }

    private sealed class FakeApprovalRunner : IApprovalSessionRunner
    {
        public bool Busy { get; set; }
        public Exception? ResumeException { get; set; }
        public int ResumeCalls { get; private set; }
        public int CancelDrainCalls { get; private set; }

        public bool IsSessionBusy(string sessionId) => Busy;

        public Task<SessionDetail> ResumeApprovalAsync(
            string sessionId,
            string entryId,
            bool approved,
            CancellationToken cancellationToken,
            string? selected = null)
        {
            ResumeCalls++;
            if (ResumeException is not null)
            {
                throw ResumeException;
            }

            return Task.FromResult(new SessionDetail
            {
                Header = new SessionHeader
                {
                    SessionId = sessionId,
                    AgentId = "coder",
                    Title = "t",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                },
                Entries = []
            });
        }

        public Task<bool> CancelAndDrainAsync(
            string sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CancelDrainCalls++;
            return Task.FromResult(true);
        }
    }
}
