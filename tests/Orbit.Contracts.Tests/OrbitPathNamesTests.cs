using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class OrbitPathNamesTests
{
    [Fact]
    public void SanitizeFileName_replaces_invalid_chars()
    {
        var raw = "a/b:c*d";
        var sanitized = OrbitPathNames.SanitizeFileName(raw);
        Assert.DoesNotContain('/', sanitized);
        Assert.DoesNotContain(':', sanitized);
        Assert.DoesNotContain('*', sanitized);
        Assert.Contains('_', sanitized);
    }

    [Fact]
    public void SanitizeFileName_collapses_parent_segments()
    {
        Assert.DoesNotContain("..", OrbitPathNames.SanitizeFileName("../x"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("coder", true)]
    [InlineData("mail-agent", true)]
    [InlineData("../evil", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void IsSafeAgentId_whitelist(string id, bool ok) =>
        Assert.Equal(ok, OrbitPathNames.IsSafeAgentId(id));

    /// <summary>
    /// Intentional: agent runtime treats ORBIT_ROOT as the data root even when a contentRoot
    /// argument is passed (HarnessProfile / MailboxRegistry / ResearchPipeline). Do not "fix"
    /// this to prefer the explicit argument — that would change production path resolution.
    /// Callers that need an explicit root must bypass ResolveDataRoot (see TryReadManifestHarness).
    /// </summary>
    [Fact]
    public void ResolveDataRoot_prefers_ORBIT_ROOT_over_explicit_contentRoot()
    {
        var prev = Environment.GetEnvironmentVariable("ORBIT_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_ROOT", @"D:\OrbitRoot");
            Assert.Equal(@"D:\OrbitRoot", OrbitPathNames.ResolveDataRoot(@"C:\fallback"));
            Assert.NotEqual(@"C:\fallback", OrbitPathNames.ResolveDataRoot(@"C:\fallback"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_ROOT", prev);
        }
    }

    [Fact]
    public void ResolveDataRoot_falls_back_to_content_root()
    {
        var prev = Environment.GetEnvironmentVariable("ORBIT_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_ROOT", null);
            Assert.Equal(@"C:\fallback", OrbitPathNames.ResolveDataRoot(@"C:\fallback"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_ROOT", prev);
        }
    }

    [Fact]
    public void IsUnder_accepts_child_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-under", "repo");
        Assert.True(OrbitPathNames.IsUnder(Path.Combine(root, "src", "a.cs"), root));
        Assert.True(OrbitPathNames.IsUnder(Path.Combine(root, "src"), root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void IsUnder_rejects_sibling_with_shared_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-under", "repo");
        var sibling = Path.Combine(Path.GetTempPath(), "orbit-under", "repo2");
        Assert.False(OrbitPathNames.IsUnder(Path.Combine(sibling, "a.cs"), root));
    }

    [Fact]
    public void IsUnder_rejects_root_itself_and_ancestors()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-under", "repo");
        Assert.False(OrbitPathNames.IsUnder(root, root));
        Assert.False(OrbitPathNames.IsUnder(Path.GetTempPath(), root));
        Assert.False(OrbitPathNames.IsUnder(null, root));
        Assert.False(OrbitPathNames.IsUnder(Path.Combine(root, "x"), ""));
    }

    [Fact]
    public void IsUnder_handles_drive_root()
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        Assert.True(OrbitPathNames.IsUnder(Path.Combine(driveRoot, "orbit-probe"), driveRoot));
        Assert.False(OrbitPathNames.IsUnder(driveRoot, driveRoot));
    }

    [Fact]
    public void IsUnderOrEqual_accepts_root_and_child()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-under-eq", "repo");
        Assert.True(OrbitPathNames.IsUnderOrEqual(root, root));
        Assert.True(OrbitPathNames.IsUnderOrEqual(Path.Combine(root, "a.cs"), root));
        Assert.False(OrbitPathNames.IsUnderOrEqual(Path.Combine(root + "2", "a.cs"), root));
    }
}
