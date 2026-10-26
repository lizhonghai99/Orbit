using Microsoft.Extensions.AI;
using Orbit.Agents.Hosting;
using Orbit.Agents.Hosting.Mcp;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

[Collection("orbit-env")]
public class McpToolHostTests
{
    [Fact]
    public async Task LoadAsync_untrusted_does_not_connect_and_exposes_no_tools()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-mcp-untrusted-" + Guid.NewGuid().ToString("N"));
        // Unique agent id avoids static ByAgent cache collisions across parallel/sequential runs.
        var agentId = "research-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data", "mcp"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "data", "mcp", agentId + ".json"),
                """
                {
                  "servers": [{
                    "id": "tavily",
                    "transport": "http",
                    "url": "https://mcp.tavily.com/mcp/",
                    "trusted": false,
                    "enabled": true
                  }]
                }
                """,
                TestContext.Current.CancellationToken);

            var prev = Environment.GetEnvironmentVariable("ORBIT_ROOT");
            Environment.SetEnvironmentVariable("ORBIT_ROOT", root);
            try
            {
                var result = await McpToolHost.LoadAsync(
                    agentId,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(result.Configured);
                Assert.Empty(result.Tools);
                Assert.Single(result.Servers);
                Assert.Equal("untrusted", result.Servers[0].Status);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ORBIT_ROOT", prev);
                var host = McpToolHost.TryGet(agentId);
                if (host is not null)
                {
                    await host.DisposeAsync();
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void UniqueName_respects_reserved_domain_names()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "run_research_pipeline"
        };
        Assert.Equal(
            "run_research_pipeline_2",
            McpVariableExpansion.UniqueName("run_research_pipeline", used));
    }

    [Theory]
    [InlineData("npx", "cmd.exe")]
    [InlineData("npm", "cmd.exe")]
    public void NormalizeStdioCommand_windows_uses_cmd(string command, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("cmd.exe wrapping is Windows-specific.");
        }

        var (cmd, args) = McpToolHost.NormalizeStdioCommand(command, ["-y", "pkg"]);
        Assert.Equal(expected, cmd);
        Assert.Equal("/c", args[0]);
        Assert.Equal(command, args[1]);
    }

    [Fact]
    public void Describe_marks_mcp_names_and_DomainOnly_filters()
    {
        var mcpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tavily_search" };
        var domain = AIFunctionFactory.Create(() => 1, name: "run_research_pipeline");
        var mcp = AIFunctionFactory.Create(() => 2, name: "tavily_search");
        var schemas = AgentToolSchemaCatalog.Describe([domain, mcp], mcpNames);
        Assert.Equal("domain", schemas.Single(s => s.Name == "run_research_pipeline").Source);
        Assert.Equal("mcp", schemas.Single(s => s.Name == "tavily_search").Source);
        Assert.Single(AgentToolSchemaCatalog.DomainOnly(schemas));
    }
}
