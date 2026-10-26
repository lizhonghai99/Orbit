using System.Text.Json;
using Orbit.Agents.Hosting;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

[Collection("orbit-env")]
public class ManifestHarnessResolveTests : IDisposable
{
    private readonly string _root;

    public ManifestHarnessResolveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-manifest-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
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
    public void TryReadManifestHarness_reads_agents_json()
    {
        var previousRoot = Environment.GetEnvironmentVariable("ORBIT_ROOT");
        var previousHarness = Environment.GetEnvironmentVariable("ORBIT_HARNESS");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", null);
            Environment.SetEnvironmentVariable("ORBIT_ROOT", _root);
            File.WriteAllText(
                Path.Combine(_root, "agents", "coder.agent.json"),
                JsonSerializer.Serialize(new AgentManifest
                {
                    Id = "coder",
                    DisplayName = "Coder",
                    Harness = ["mode", "todo", "file_memory", "file_access", "skills", "compaction", "tool_approval"]
                }));

            var fromFile = HarnessProfile.TryReadManifestHarness("coder", _root);
            Assert.NotNull(fromFile);
            Assert.Contains("file_access", fromFile!);
            Assert.Contains("file_memory", fromFile);

            var enabled = HarnessProfile.ResolveEnabled(agentId: "coder");
            Assert.Contains("file_access", enabled);
            Assert.Contains("file_memory", enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("ORBIT_HARNESS", previousHarness);
        }
    }
}
