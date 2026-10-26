using Orbit.Agents.Hosting;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class OrbitWorkspaceRootsTests
{
    [Fact]
    public void ResolveShellRoot_prefers_RunContext_over_fallback()
    {
        var fallback = Path.Combine(Path.GetTempPath(), "orbit-shell-fallback");
        using (OrbitRunContext.Push(null, null, Path.Combine(Path.GetTempPath(), "orbit-run-ws"), null))
        {
            var root = OrbitWorkspaceRoots.ResolveShellRoot(fallback);
            Assert.Contains("orbit-run-ws", root, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(Path.GetFullPath(fallback), OrbitWorkspaceRoots.ResolveShellRoot(fallback));
    }

    [Fact]
    public void ResolveFileAccessRoot_uses_ambient_when_set()
    {
        var ws = Path.Combine(Path.GetTempPath(), "orbit-fa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        try
        {
            using (OrbitRunContext.Push("r", "s", ws, "wid"))
            {
                var root = OrbitWorkspaceRoots.ResolveFileAccessRoot(null);
                Assert.Equal(Path.GetFullPath(ws), root);
            }
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { /* ignore */ }
        }
    }
}
