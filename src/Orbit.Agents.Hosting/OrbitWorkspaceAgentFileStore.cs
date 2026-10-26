using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace Orbit.Agents.Hosting;

/// <summary>
/// <see cref="AgentFileStore"/> that rebinds to the ambient workspace root each call
/// (<see cref="OrbitRunContext.Workspace"/> → env → contentRoot default).
/// </summary>
#pragma warning disable MAAI001 // FileSystemAgentFileStore experimental
public sealed class OrbitWorkspaceAgentFileStore(string? contentRoot) : AgentFileStore
{
    private const int MaxCachedRoots = 8;
    private readonly ConcurrentDictionary<string, FileSystemAgentFileStore> _byRoot =
        new(StringComparer.OrdinalIgnoreCase);

    private FileSystemAgentFileStore Current()
    {
        var root = OrbitWorkspaceRoots.ResolveFileAccessRoot(contentRoot);
        // Create on demand — FileAccess root can appear after process start (workspace switch / optional).
        Directory.CreateDirectory(root);

        if (_byRoot.Count >= MaxCachedRoots && !_byRoot.ContainsKey(root))
        {
            _byRoot.Clear();
        }

        return _byRoot.GetOrAdd(root, static r => new FileSystemAgentFileStore(r));
    }

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        Current().WriteAsync(path, content, cancellationToken);

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Current().ReadAsync(path, cancellationToken);

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        Current().DeleteAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default) =>
        Current().ListChildrenAsync(directory, cancellationToken);

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Current().FileExistsAsync(path, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        Current().CreateDirectoryAsync(path, cancellationToken);
}
#pragma warning restore MAAI001
