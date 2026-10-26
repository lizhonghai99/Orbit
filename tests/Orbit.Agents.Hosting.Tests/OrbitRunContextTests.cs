using Orbit.Agents.Hosting;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class OrbitRunContextTests
{
    [Fact]
    public void Push_sets_workspace_and_restores_on_dispose()
    {
        Assert.Null(OrbitRunContext.Workspace);
        using (OrbitRunContext.Push("run1", "sess1", @"D:\ws-a", "id-a"))
        {
            Assert.Equal("run1", OrbitRunContext.RunId);
            Assert.Equal("sess1", OrbitRunContext.SessionId);
            Assert.Equal(@"D:\ws-a", OrbitRunContext.Workspace);
            Assert.Equal("id-a", OrbitRunContext.WorkspaceId);

            using (OrbitRunContext.Push("run2", "sess2", @"D:\ws-b", "id-b"))
            {
                Assert.Equal(@"D:\ws-b", OrbitRunContext.Workspace);
            }

            Assert.Equal(@"D:\ws-a", OrbitRunContext.Workspace);
        }

        Assert.Null(OrbitRunContext.Workspace);
        Assert.Null(OrbitRunContext.RunId);
    }
}
