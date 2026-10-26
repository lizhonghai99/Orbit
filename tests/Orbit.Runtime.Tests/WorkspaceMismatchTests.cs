using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Agui;
using Xunit;

namespace Orbit.Runtime.Tests;

public class WorkspaceMismatchTests
{
    [Fact]
    public void EnsureWorkspaceAligned_same_path_ok()
    {
        var agent = new AgentRegistry.TrackedAgent(new AgentManifest
        {
            Id = "coder",
            DisplayName = "x",
            Project = "x.csproj",
            Workspace = "required"
        })
        {
            StartedWorkspacePath = @"D:\ws-a"
        };

        var header = new SessionHeader
        {
            SessionId = "s",
            AgentId = "coder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkspacePath = @"D:\ws-a"
        };

        AguiSessionRunner.EnsureWorkspaceAligned(header, agent);
    }

    [Fact]
    public void EnsureWorkspaceAligned_mismatch_throws()
    {
        var agent = new AgentRegistry.TrackedAgent(new AgentManifest
        {
            Id = "coder",
            DisplayName = "x",
            Project = "x.csproj",
            Workspace = "required"
        })
        {
            StartedWorkspacePath = @"D:\ws-b"
        };

        var header = new SessionHeader
        {
            SessionId = "s",
            AgentId = "coder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkspacePath = @"D:\ws-a"
        };

        var ex = Assert.Throws<OrbitApiException>(() =>
            AguiSessionRunner.EnsureWorkspaceAligned(header, agent));
        Assert.True(ex.Is(OrbitErrorCodes.WorkspaceMismatch));
        Assert.StartsWith("workspace_mismatch", ex.Message);
    }

    [Fact]
    public void EnsureWorkspaceAligned_mail_none_skips()
    {
        var agent = new AgentRegistry.TrackedAgent(new AgentManifest
        {
            Id = "mail",
            DisplayName = "x",
            Project = "x.csproj",
            Workspace = "none"
        })
        {
            StartedWorkspacePath = null
        };

        var header = new SessionHeader
        {
            SessionId = "s",
            AgentId = "mail",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkspacePath = @"D:\ws-a"
        };

        AguiSessionRunner.EnsureWorkspaceAligned(header, agent);
    }
}
