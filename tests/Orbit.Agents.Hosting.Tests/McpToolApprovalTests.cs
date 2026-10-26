using Orbit.Agents.Hosting.Mcp;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class McpToolApprovalTests
{
    [Fact]
    public void ToolNeedsApproval_blanket_flag_when_list_empty()
    {
        var cfg = new McpServerConfig { Id = "s", RequiresApproval = true, ApprovalTools = [] };
        Assert.True(McpToolHost.ToolNeedsApproval(cfg, "search"));
        Assert.True(McpToolHost.ToolNeedsApproval(cfg, "write"));
    }

    [Fact]
    public void ToolNeedsApproval_list_overrides_blanket()
    {
        var cfg = new McpServerConfig
        {
            Id = "s",
            RequiresApproval = false,
            ApprovalTools = ["write_file", "delete"]
        };
        Assert.False(McpToolHost.ToolNeedsApproval(cfg, "search"));
        Assert.True(McpToolHost.ToolNeedsApproval(cfg, "write_file"));
        Assert.True(McpToolHost.ToolNeedsApproval(cfg, "DELETE"));
    }
}
