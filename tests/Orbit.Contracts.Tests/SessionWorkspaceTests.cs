using System.Text.Json;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class SessionWorkspaceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void SessionHeader_roundtrips_workspace_fields()
    {
        var header = new SessionHeader
        {
            SessionId = Guid.NewGuid().ToString("N"),
            AgentId = "coder",
            Title = "New chat",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = "abc123",
            WorkspacePath = @"D:\LearnCode\Demo"
        };

        var json = JsonSerializer.Serialize(header, JsonOptions);
        var back = JsonSerializer.Deserialize<SessionHeader>(json, JsonOptions);

        Assert.NotNull(back);
        Assert.Equal(header.WorkspaceId, back!.WorkspaceId);
        Assert.Equal(header.WorkspacePath, back.WorkspacePath);
    }

    [Fact]
    public void AgentManifest_workspace_defaults_to_optional()
    {
        var manifest = JsonSerializer.Deserialize<AgentManifest>(
            """
            {
              "id": "x",
              "displayName": "X",
              "project": "src/X/X.csproj",
              "protocol": "ag-ui"
            }
            """,
            JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal("optional", manifest!.Workspace);
    }

    [Fact]
    public void AgentManifest_reads_workspace_policy()
    {
        var manifest = JsonSerializer.Deserialize<AgentManifest>(
            """
            {
              "id": "coder",
              "displayName": "x",
              "project": "src/Orbit.Agents.Coder/Orbit.Agents.Coder.csproj",
              "protocol": "ag-ui",
              "workspace": "required"
            }
            """,
            JsonOptions);

        Assert.NotNull(manifest);
        Assert.Equal("required", manifest!.Workspace);
    }

    [Fact]
    public void SessionSummary_includes_workspace_path()
    {
        var summary = new SessionSummary
        {
            SessionId = "s1",
            AgentId = "coder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkspacePath = @"D:\ws"
        };

        var json = JsonSerializer.Serialize(summary, JsonOptions);
        Assert.Contains("workspacePath", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<SessionSummary>(json, JsonOptions);
        Assert.Equal(@"D:\ws", back!.WorkspacePath);
    }
}
