using Orbit.Agents.Hosting;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

[Collection("orbit-env")]
public class FileAccessMountUsableTests
{
    [Fact]
    public async Task OrbitWorkspaceAgentFileStore_creates_missing_root_on_first_use()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-fa-store-" + Guid.NewGuid().ToString("N"));
        var previousWs = Environment.GetEnvironmentVariable("ORBIT_WORKSPACE");
        var previousFa = Environment.GetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_WORKSPACE", null);
            Environment.SetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT", root);
            Assert.False(Directory.Exists(root));
#pragma warning disable MAAI001
            var store = new OrbitWorkspaceAgentFileStore(contentRoot: null);
            await store.CreateDirectoryAsync("notes", TestContext.Current.CancellationToken);
#pragma warning restore MAAI001
            Assert.True(Directory.Exists(root));
            Assert.True(Directory.Exists(Path.Combine(root, "notes")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_WORKSPACE", previousWs);
            Environment.SetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT", previousFa);
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
