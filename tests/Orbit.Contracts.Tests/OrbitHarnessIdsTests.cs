using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class OrbitHarnessIdsTests
{
    [Fact]
    public void ToEnvCsv_empty_uses_default()
    {
        Assert.Equal("mode,todo,skills,compaction,tool_approval", OrbitHarnessIds.ToEnvCsv(null));
        Assert.Equal("mode,todo,skills,compaction,tool_approval", OrbitHarnessIds.ToEnvCsv([]));
    }

    [Fact]
    public void ToEnvCsv_normalizes_aliases_and_dedupes()
    {
        var csv = OrbitHarnessIds.ToEnvCsv(["Mode", "background_agents", "file-access", "mode"]);
        Assert.Equal("mode,background,file_access", csv);
    }

    [Fact]
    public void Normalize_accepts_compaction_and_tool_approval()
    {
        Assert.Equal(OrbitHarnessIds.Compaction, OrbitHarnessIds.Normalize("compaction"));
        Assert.Equal(OrbitHarnessIds.ToolApproval, OrbitHarnessIds.Normalize("tool_approval"));
        Assert.Equal(OrbitHarnessIds.ToolApproval, OrbitHarnessIds.Normalize("approval"));
    }

    [Fact]
    public void Normalize_rejects_unknown()
    {
        Assert.Null(OrbitHarnessIds.Normalize("not_a_module"));
        Assert.Null(OrbitHarnessIds.Normalize(""));
    }
}
