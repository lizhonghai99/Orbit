using Orbit.Contracts;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Workspaces;
using Xunit;

namespace Orbit.Runtime.Tests;

public class WorkspaceMissingActivateTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceService _sut;

    public WorkspaceMissingActivateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-ws-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _sut = new WorkspaceService(new RepoPaths { RepoRoot = _root });
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
    public void Activate_deleted_directory_throws_workspace_missing()
    {
        var dir = Path.Combine(_root, "gone");
        Directory.CreateDirectory(dir);
        var opened = _sut.Open(new OpenWorkspaceRequest { Path = dir });
        Directory.Delete(dir);

        var ex = Assert.Throws<ArgumentException>(() => _sut.Activate(opened.Id));
        Assert.Contains("workspace_missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_active_throws()
    {
        var dir = Path.Combine(_root, "keep");
        Directory.CreateDirectory(dir);
        var opened = _sut.Open(new OpenWorkspaceRequest { Path = dir });
        var ex = Assert.Throws<InvalidOperationException>(() => _sut.Remove(opened.Id));
        Assert.Contains("active workspace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupt_json_is_backed_up_and_throws()
    {
        var file = Path.Combine(_root, "data", "workspaces.json");
        File.WriteAllText(file, "{ not-json");
        Assert.Throws<InvalidDataException>(() => _sut.GetState());
        var backups = Directory.GetFiles(Path.Combine(_root, "data"), "workspaces.json.corrupt.*");
        Assert.NotEmpty(backups);
    }
}
