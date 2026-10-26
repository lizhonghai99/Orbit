using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class ApprovalTransitionsTests
{
    [Theory]
    [InlineData(ApprovalSources.Tool, true)]
    [InlineData("maf", true)]
    [InlineData("mail", false)]
    [InlineData(null, false)]
    public void IsToolInterrupt_accepts_current_and_legacy(string? source, bool expected) =>
        Assert.Equal(expected, ApprovalSources.IsToolInterrupt(source));

    [Theory]
    [InlineData(ApprovalStatus.Pending, true)]
    [InlineData(ApprovalStatus.Failed, true)]
    [InlineData(ApprovalStatus.Approving, false)]
    [InlineData(ApprovalStatus.Approved, false)]
    [InlineData(ApprovalStatus.Rejected, false)]
    [InlineData(ApprovalStatus.Queued, false)]
    public void CanApprove_only_pending_or_failed(ApprovalStatus status, bool expected)
    {
        Assert.Equal(expected, ApprovalTransitions.CanApprove(status));
        Assert.Equal(expected, ApprovalTransitions.CanReject(status));
    }

    [Fact]
    public void Every_status_is_covered_by_approve_and_reject()
    {
        foreach (var status in Enum.GetValues<ApprovalStatus>())
        {
            var allowed = status is ApprovalStatus.Pending or ApprovalStatus.Failed;
            Assert.Equal(allowed, ApprovalTransitions.CanApprove(status));
            Assert.Equal(allowed, ApprovalTransitions.CanReject(status));
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CanPromoteQueue_only_when_no_active_card(bool hasActiveCard, bool expected) =>
        Assert.Equal(expected, ApprovalTransitions.CanPromoteQueue(hasActiveCard));

    [Theory]
    [InlineData("pending", true)]
    [InlineData("FAILED", true)]
    [InlineData("approving", true)] // self-heal after stuck mid-flight
    [InlineData("approved", false)]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    public void CanApproveOrRetry_string_statuses(string? status, bool expected)
    {
        Assert.Equal(expected, ApprovalTransitions.CanApproveOrRetry(status));
        Assert.Equal(expected, ApprovalTransitions.CanReject(status));
    }

    [Theory]
    [InlineData("pending", true)]
    [InlineData("failed", true)]
    [InlineData("approving", true)]
    [InlineData("approved", false)]
    [InlineData("rejected", false)]
    [InlineData("queued", false)]
    public void IsActiveCard(string status, bool expected) =>
        Assert.Equal(expected, ApprovalTransitions.IsActiveCard(status));

    [Fact]
    public void CanPromoteQueued_false_when_any_active()
    {
        Assert.False(ApprovalTransitions.CanPromoteQueued(["queued", "pending"]));
        Assert.True(ApprovalTransitions.CanPromoteQueued(["queued", "approved"]));
    }

    [Theory]
    [InlineData("reject", ApprovalStatuses.Rejected)]
    [InlineData("approve", ApprovalStatuses.Approving)]
    [InlineData("retry", ApprovalStatuses.Approving)]
    public void NextStatusAfterDecision(string decision, string expected) =>
        Assert.Equal(expected, ApprovalTransitions.NextStatusAfterDecision(decision));

    [Theory]
    [InlineData("aproved")]
    [InlineData("yes")]
    [InlineData("")]
    public void NextStatusAfterDecision_rejects_unknown(string decision) =>
        Assert.Throws<ArgumentException>(() => ApprovalTransitions.NextStatusAfterDecision(decision));

    [Theory]
    [InlineData(null, ApprovalStatuses.Pending)]
    [InlineData("  ", ApprovalStatuses.Pending)]
    [InlineData("APPROVED", "approved")]
    public void Normalize(string? status, string expected) =>
        Assert.Equal(expected, ApprovalTransitions.Normalize(status));
}
