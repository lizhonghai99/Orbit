using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

public class ApprovalWorkflowWorkspaceTests
{
    [Fact]
    public void BeginDecision_preserves_workspace_snapshot()
    {
        var header = new SessionHeader
        {
            SessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            AgentId = "coder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = "ws-1",
            WorkspacePath = @"D:\LearnCode\Orbit"
        };

        var approval = new SessionEntry
        {
            Id = "e1",
            Type = "approval",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                status = ApprovalStatuses.Pending,
                source = ApprovalSources.Tool,
                toolName = "file_access_write",
                text = "write?"
            })
        };

        var detail = new SessionDetail { Header = header, Entries = [approval] };
        var next = ApprovalWorkflow.BeginDecision(
            detail,
            "e1",
            new DecideApprovalRequest { Decision = "approve" });

        Assert.Equal("ws-1", next.Header.WorkspaceId);
        Assert.Equal(@"D:\LearnCode\Orbit", next.Header.WorkspacePath);
        Assert.Equal("coder", next.Header.AgentId);
    }

    [Fact]
    public void BeginDecision_preserves_opaque_payload_fields()
    {
        var approval = new SessionEntry
        {
            Id = "e1",
            Type = "approval",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                status = ApprovalStatuses.Pending,
                source = ApprovalSources.Tool,
                toolName = "archive_mail",
                subject = "Invoice",
                from = "a@b.com",
                targetFolder = "Archive",
                customAgentField = 42
            })
        };

        var detail = new SessionDetail
        {
            Header = new SessionHeader
            {
                SessionId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                AgentId = "mail",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            Entries = [approval]
        };

        var next = ApprovalWorkflow.BeginDecision(
            detail,
            "e1",
            new DecideApprovalRequest { Decision = "approve" });

        var payload = Assert.IsType<JsonElement>(next.Entries[0].Payload);
        Assert.Equal(ApprovalStatuses.Approving, payload.GetProperty("status").GetString());
        Assert.Equal("Invoice", payload.GetProperty("subject").GetString());
        Assert.Equal("a@b.com", payload.GetProperty("from").GetString());
        Assert.Equal("Archive", payload.GetProperty("targetFolder").GetString());
        Assert.Equal(42, payload.GetProperty("customAgentField").GetInt32());
    }

    [Fact]
    public void BeginDecision_rejects_typo_decision_on_legacy_card()
    {
        var approval = new SessionEntry
        {
            Id = "e1",
            Type = "approval",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                status = ApprovalStatuses.Pending,
                source = "legacy",
                text = "confirm?"
            })
        };

        var detail = new SessionDetail
        {
            Header = new SessionHeader
            {
                SessionId = "cccccccccccccccccccccccccccccccc",
                AgentId = "mail",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            Entries = [approval]
        };

        var ex = Assert.Throws<ArgumentException>(() =>
            ApprovalWorkflow.BeginDecision(
                detail,
                "e1",
                new DecideApprovalRequest { Decision = "aproved" }));
        Assert.Contains("approve", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
