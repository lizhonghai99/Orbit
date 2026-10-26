using Orbit.Agents.Hosting.Mcp;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class McpVariableExpansionTests
{
    [Fact]
    public void TryExpand_passthrough_without_placeholder()
    {
        Assert.True(McpVariableExpansion.TryExpand("plain", out var resolved, out var missing));
        Assert.Equal("plain", resolved);
        Assert.Null(missing);
    }

    [Fact]
    public void TryExpand_missing_env_fails()
    {
        var name = "ORBIT_MCP_TEST_MISSING_" + Guid.NewGuid().ToString("N");
        Assert.False(McpVariableExpansion.TryExpand($"Bearer ${{{name}}}", out var resolved, out var missing));
        Assert.Null(resolved);
        Assert.Equal(name, missing);
    }

    [Fact]
    public void TryExpand_resolves_process_env()
    {
        var name = "ORBIT_MCP_TEST_PRESENT_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "secret-value");
        try
        {
            Assert.True(McpVariableExpansion.TryExpand($"Bearer ${{{name}}}", out var resolved, out _));
            Assert.Equal("Bearer secret-value", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void TryExpandEnvMap_reports_missing_env()
    {
        var name = "ORBIT_MCP_TEST_ENV_" + Guid.NewGuid().ToString("N");
        var ok = McpVariableExpansion.TryExpandEnvMap(
            new Dictionary<string, string> { ["KEY"] = $"${{{name}}}" },
            out _,
            out var error);
        Assert.False(ok);
        Assert.Equal($"missing env {name}", error);
    }

    [Fact]
    public void TryExpandHeaderMap_resolves_placeholder()
    {
        var name = "ORBIT_MCP_TEST_HDR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "tok");
        try
        {
            Assert.True(McpVariableExpansion.TryExpandHeaderMap(
                new Dictionary<string, string> { ["Authorization"] = $"Bearer ${{{name}}}" },
                out var headers,
                out _));
            Assert.Equal("Bearer tok", headers["Authorization"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void UniqueName_appends_suffix_on_conflict()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tavily_search" };
        Assert.Equal("tavily_search_2", McpVariableExpansion.UniqueName("tavily_search", used));
        used.Add("tavily_search_2");
        Assert.Equal("tavily_search_3", McpVariableExpansion.UniqueName("tavily_search", used));
    }

    [Fact]
    public void SanitizeToolPrefix_null_uses_mcp_id()
    {
        Assert.Equal("mcp_tavily_", McpVariableExpansion.SanitizeToolPrefix(null, "tavily"));
    }

    [Fact]
    public void SanitizeToolPrefix_empty_keeps_empty()
    {
        Assert.Equal("", McpVariableExpansion.SanitizeToolPrefix("", "tavily"));
    }

    [Fact]
    public void RedactBearer_masks_token()
    {
        var redacted = McpVariableExpansion.RedactBearer("fail Bearer abc.def.ghi more");
        Assert.DoesNotContain("abc.def.ghi", redacted);
        Assert.Contains("Bearer ***", redacted);
    }
}
