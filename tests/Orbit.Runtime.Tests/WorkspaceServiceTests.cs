using System.Diagnostics;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Workspaces;
using Xunit;

namespace Orbit.Runtime.Tests;

public class WorkspaceServiceTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceService _sut;

    public WorkspaceServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-ws-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
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
    public void Open_missing_directory_throws()
    {
        var missing = Path.Combine(_root, "no-such-folder");
        Assert.Throws<ArgumentException>(() =>
            _sut.Open(new OpenWorkspaceRequest { Path = missing }));
    }

    [Fact]
    public void Open_same_path_reuses_id()
    {
        var dir = Path.Combine(_root, "proj-a");
        Directory.CreateDirectory(dir);

        var first = _sut.Open(new OpenWorkspaceRequest { Path = dir });
        var second = _sut.Open(new OpenWorkspaceRequest { Path = dir });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, _sut.GetState().ActiveId);
        Assert.Single(_sut.GetState().Items);
    }

    [Fact]
    public void Activate_unknown_throws()
    {
        Assert.Throws<KeyNotFoundException>(() => _sut.Activate(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Remove_active_is_rejected()
    {
        var dir = Path.Combine(_root, "proj-b");
        Directory.CreateDirectory(dir);
        var opened = _sut.Open(new OpenWorkspaceRequest { Path = dir });

        Assert.Throws<InvalidOperationException>(() => _sut.Remove(opened.Id));
        Assert.Equal(opened.Id, _sut.GetState().ActiveId);
    }

    [Fact]
    public void Remove_non_active_ok()
    {
        var a = Path.Combine(_root, "proj-a2");
        var b = Path.Combine(_root, "proj-b2");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var first = _sut.Open(new OpenWorkspaceRequest { Path = a });
        _sut.Open(new OpenWorkspaceRequest { Path = b });
        _sut.Remove(first.Id);
        Assert.DoesNotContain(_sut.GetState().Items, i => i.Id == first.Id);
    }

    [Fact]
    public void Open_rejects_drive_root()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Drive-root rejection is Windows-specific.");
        }

        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        Assert.Throws<ArgumentException>(() =>
            _sut.Open(new OpenWorkspaceRequest { Path = root }));
    }

    [Fact]
    public void Open_defaults_name_to_directory_name()
    {
        var dir = Path.Combine(_root, "MyProject");
        Directory.CreateDirectory(dir);
        var opened = _sut.Open(new OpenWorkspaceRequest { Path = dir });
        Assert.Equal("MyProject", opened.Name);
    }

    [Fact]
    public void Open_symlink_into_system_dir_rejected()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(win) || !Directory.Exists(win))
        {
            Assert.Skip("Windows directory unavailable.");
        }

        var link = Path.Combine(_root, "evil-link-" + Guid.NewGuid().ToString("N"));
        EnsureDirLink(link, win);

        try
        {
            var ex = Assert.Throws<ArgumentException>(() =>
                _sut.Open(new OpenWorkspaceRequest { Path = link }));
            Assert.Contains("system directory", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteLink(link);
        }
    }

    [Fact]
    public void RejectUnsafeRoots_resolves_symlink_target()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(win) || !Directory.Exists(win))
        {
            Assert.Skip("Windows directory unavailable.");
        }

        var link = Path.Combine(_root, "resolve-link-" + Guid.NewGuid().ToString("N"));
        EnsureDirLink(link, win);

        try
        {
            Assert.Throws<ArgumentException>(() => WorkspaceService.RejectUnsafeRoots(link));
        }
        finally
        {
            TryDeleteLink(link);
        }
    }

    [Fact]
    public void RejectUnsafeRoots_rejects_path_under_ancestor_junction()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(win) || !Directory.Exists(win))
        {
            Assert.Skip("Windows directory unavailable.");
        }

        var link = Path.Combine(_root, "ancestor-link-" + Guid.NewGuid().ToString("N"));
        EnsureDirLink(link, win);

        // Leaf is not a reparse point; escape is via ancestor junction.
        var nested = Path.Combine(link, "System32");
        if (!Directory.Exists(nested))
        {
            nested = link; // still reject the link itself via ExpandResolvedPrefixes
        }

        try
        {
            var ex = Assert.Throws<ArgumentException>(() => WorkspaceService.RejectUnsafeRoots(nested));
            Assert.Contains("system directory", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteLink(link);
        }
    }

    /// <summary>
    /// Symlink when privileged; otherwise <c>mklink /J</c> (junction, no elevation).
    /// On CI, failure to create either is a hard fail so skips cannot hide regressions.
    /// </summary>
    private static void EnsureDirLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            // Windows without SeCreateSymbolicLinkPrivilege: API may not throw but link is missing.
            if (Directory.Exists(linkPath))
            {
                return;
            }

            try
            {
                Directory.Delete(linkPath);
            }
            catch
            {
                // best-effort; junction create will fail clearly if path is stuck
            }
        }
        catch
        {
            // fall through to junction
        }

        if (OperatingSystem.IsWindows() && TryCreateJunction(linkPath, targetPath))
        {
            return;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        {
            Assert.Fail(
                $"Could not create symlink or junction at '{linkPath}' → '{targetPath}'. "
                + "CI must exercise RejectUnsafeRoots reparse coverage.");
        }

        Assert.Skip("Symlink/junction creation unavailable (elevation?).");
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteLink(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
