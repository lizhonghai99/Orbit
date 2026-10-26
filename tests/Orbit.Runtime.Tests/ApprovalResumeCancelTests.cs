using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Agui;
using Xunit;

namespace Orbit.Runtime.Tests;

public class ApprovalResumeCancelTests
{
    [Fact]
    public void TurnEndedAsUserCancelled_true_on_run_interrupted_cancelled()
    {
        var detail = Detail(
            Meta(new { kind = SessionMetaKinds.ToolApprovalResume, entryId = "a1" }),
            Meta(new { kind = SessionMetaKinds.RunInterrupted, reason = "cancelled" }));

        Assert.True(AguiSessionRunner.TurnEndedAsUserCancelled(detail));
    }

    [Fact]
    public void TurnEndedAsUserCancelled_false_on_finished()
    {
        var detail = Detail(Meta(new { kind = SessionMetaKinds.AguiRunFinished }));
        Assert.False(AguiSessionRunner.TurnEndedAsUserCancelled(detail));
    }

    [Fact]
    public void TurnEndedAsUserCancelled_false_on_error_interrupt()
    {
        var detail = Detail(Meta(new { kind = SessionMetaKinds.RunInterrupted, reason = "error" }));
        Assert.False(AguiSessionRunner.TurnEndedAsUserCancelled(detail));
    }

    [Fact]
    public void ThrowIfApprovalResumeCancelled_throws_on_cancelled_interrupt()
    {
        var detail = Detail(Meta(new { kind = SessionMetaKinds.RunInterrupted, reason = "cancelled" }));
        Assert.Throws<OperationCanceledException>(() =>
            AguiSessionRunner.ThrowIfApprovalResumeCancelled(detail));
    }

    [Fact]
    public void ThrowIfApprovalResumeCancelled_noop_on_finished()
    {
        var detail = Detail(Meta(new { kind = SessionMetaKinds.AguiRunFinished }));
        AguiSessionRunner.ThrowIfApprovalResumeCancelled(detail);
    }

    [Fact]
    public void TurnEndedAsDisconnected_true_on_disconnected_interrupt()
    {
        var detail = Detail(Meta(new { kind = SessionMetaKinds.RunInterrupted, reason = "disconnected" }));
        Assert.True(AguiSessionRunner.TurnEndedAsDisconnected(detail));
        Assert.False(AguiSessionRunner.TurnEndedAsUserCancelled(detail));
    }

    [Fact]
    public void ThrowIfApprovalResumeCancelled_throws_on_disconnected()
    {
        var detail = Detail(Meta(new { kind = SessionMetaKinds.RunInterrupted, reason = "disconnected" }));
        Assert.Throws<OperationCanceledException>(() =>
            AguiSessionRunner.ThrowIfApprovalResumeCancelled(detail));
    }

    private static SessionDetail Detail(params SessionEntry[] entries) =>
        new()
        {
            Header = new SessionHeader
            {
                SessionId = "s1",
                AgentId = "coder",
                Title = "t",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            Entries = entries
        };

    private static SessionEntry Meta(object payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "meta",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload)
        };
}
