using Orbit.Agents.Hosting;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

[Collection("orbit-env")]
public class HarnessProfileTests
{
    [Fact]
    public void ResolveEnabled_default_is_lean()
    {
        var previous = Environment.GetEnvironmentVariable("ORBIT_HARNESS");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", null);
            var enabled = HarnessProfile.ResolveEnabled();
            Assert.Equal(
                new HashSet<string>(OrbitHarnessIds.Default, StringComparer.OrdinalIgnoreCase),
                enabled.ToHashSet(StringComparer.OrdinalIgnoreCase));
            Assert.Contains("compaction", enabled);
            Assert.Contains("tool_approval", enabled);
            Assert.DoesNotContain("file_access", enabled);
            Assert.DoesNotContain("file_memory", enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", previous);
        }
    }

    [Fact]
    public void ResolveEnabled_reads_ORBIT_HARNESS()
    {
        var previous = Environment.GetEnvironmentVariable("ORBIT_HARNESS");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", "mode,file_access,background");
            var enabled = HarnessProfile.ResolveEnabled();
            Assert.Contains("mode", enabled);
            Assert.Contains("file_access", enabled);
            Assert.Contains("background", enabled);
            Assert.DoesNotContain("todo", enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", previous);
        }
    }

    [Fact]
    public void Describe_only_lists_enabled_selectable_modules()
    {
        var previous = Environment.GetEnvironmentVariable("ORBIT_HARNESS");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", null);
            var dto = HarnessProfile.Describe(
                "mail",
                contentRoot: null,
                enabled: new HashSet<string>(["mode", "todo", "skills"], StringComparer.OrdinalIgnoreCase));

            var ids = dto.Modules.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("mode", ids);
            Assert.Contains("todo", ids);
            Assert.Contains("skills", ids);
            // Compaction stays opt-in; tool_approval is always shown (auto-enabled for ask_user_*).
            Assert.DoesNotContain("compaction", ids);
            Assert.Contains("tool_approval", ids);
            Assert.Contains(
                "auto-enabled",
                dto.Modules.First(m => m.Id == "tool_approval").Detail,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("file_access", ids);
            Assert.DoesNotContain("file_memory", ids);
            Assert.DoesNotContain("background_agents", ids);
            Assert.DoesNotContain("+FileAccess", dto.Style);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", previous);
        }
    }

    [Fact]
    public void Describe_prefers_effective_harness_hint_over_env()
    {
        var previous = Environment.GetEnvironmentVariable("ORBIT_HARNESS");
        const string agentId = "hint-tool-approval-agent";
        try
        {
            // Env omits tool_approval — without the hint, Detail would say auto-enabled.
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", "mode,todo,skills");
            AgentCapabilityHints.RegisterEffectiveHarness(
                agentId,
                new HashSet<string>(["mode", "todo", "skills", "tool_approval"], StringComparer.OrdinalIgnoreCase));

            var dto = HarnessProfile.Describe(agentId, contentRoot: null);
            var approval = Assert.Single(dto.Modules, m => m.Id == "tool_approval");
            Assert.True(approval.Enabled);
            Assert.DoesNotContain("auto-enabled", approval.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ApprovalRequired", approval.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", previous);
        }
    }
}
