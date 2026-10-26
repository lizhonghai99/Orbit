using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;

namespace Orbit.Runtime.Workspaces;

/// <summary>Host-level workspace registry: single active path in <c>data/workspaces.json</c>.</summary>
public sealed class WorkspaceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _filePath;

    public WorkspaceService(RepoPaths paths)
    {
        Directory.CreateDirectory(paths.DataDir);
        _filePath = Path.Combine(paths.DataDir, "workspaces.json");
    }

    public WorkspaceState GetState()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    public WorkspaceRecord? GetActive()
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            if (string.IsNullOrWhiteSpace(state.ActiveId))
            {
                return null;
            }

            return state.Items.FirstOrDefault(i =>
                string.Equals(i.Id, state.ActiveId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public WorkspaceRecord? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            return LoadUnlocked().Items.FirstOrDefault(i =>
                string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public WorkspaceRecord Open(OpenWorkspaceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new ArgumentException("path is required", nameof(request));
        }

        var full = NormalizeExistingDirectory(request.Path);
        RejectUnsafeRoots(full);

        lock (_gate)
        {
            var state = LoadUnlocked();
            var now = DateTimeOffset.UtcNow;
            var existing = state.Items.FirstOrDefault(i =>
                string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                var refreshed = new WorkspaceRecord
                {
                    Id = existing.Id,
                    Name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim(),
                    Path = existing.Path,
                    CreatedAt = existing.CreatedAt,
                    LastOpenedAt = now
                };
                ReplaceItem(state, refreshed);
                state.ActiveId = refreshed.Id;
                SaveUnlocked(state);
                return refreshed;
            }

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? new DirectoryInfo(full).Name
                : request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = full;
            }

            var created = new WorkspaceRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Path = full,
                CreatedAt = now,
                LastOpenedAt = now
            };
            state.Items.Add(created);
            state.ActiveId = created.Id;
            SaveUnlocked(state);
            return created;
        }
    }

    public WorkspaceRecord Activate(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("id is required", nameof(id));
        }

        lock (_gate)
        {
            var state = LoadUnlocked();
            var item = state.Items.FirstOrDefault(i =>
                string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                throw new KeyNotFoundException($"Unknown workspace '{id}'.");
            }

            if (!Directory.Exists(item.Path))
            {
                throw new ArgumentException($"workspace_missing: {item.Path}");
            }

            var now = DateTimeOffset.UtcNow;
            var refreshed = new WorkspaceRecord
            {
                Id = item.Id,
                Name = item.Name,
                Path = item.Path,
                CreatedAt = item.CreatedAt,
                LastOpenedAt = now
            };
            ReplaceItem(state, refreshed);
            state.ActiveId = refreshed.Id;
            SaveUnlocked(state);
            return refreshed;
        }
    }

    public void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("id is required", nameof(id));
        }

        lock (_gate)
        {
            var state = LoadUnlocked();
            var idx = state.Items.FindIndex(i =>
                string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                throw new KeyNotFoundException($"Unknown workspace '{id}'.");
            }

            if (string.Equals(state.ActiveId, id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Cannot remove the active workspace; activate another first.");
            }

            state.Items.RemoveAt(idx);
            SaveUnlocked(state);
        }
    }

    private WorkspaceState LoadUnlocked()
    {
        if (!File.Exists(_filePath))
        {
            return new WorkspaceState();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<WorkspaceState>(json, JsonOptions) ?? new WorkspaceState();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            try
            {
                var bak = _filePath + ".corrupt." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(_filePath, bak, overwrite: true);
            }
            catch
            {
                // best-effort backup
            }

            throw new InvalidDataException(
                $"Workspace registry JSON is corrupt: {_filePath}. A .corrupt.* backup was attempted.",
                ex);
        }
    }

    private void SaveUnlocked(WorkspaceState state)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        var temp = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }

    private static void ReplaceItem(WorkspaceState state, WorkspaceRecord item)
    {
        var idx = state.Items.FindIndex(i =>
            string.Equals(i.Id, item.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            state.Items[idx] = item;
        }
        else
        {
            state.Items.Add(item);
        }
    }

    private static string NormalizeExistingDirectory(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid path: {ex.Message}", nameof(path));
        }

        if (!Directory.Exists(full))
        {
            throw new ArgumentException($"Directory does not exist: {full}");
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Reject drive roots (e.g. C:\) and well-known system directories.
    /// Walks each path segment, resolves symlink/junction targets (including ancestors),
    /// and re-checks every resolved prefix (lexical path alone is not enough).
    /// </summary>
    internal static void RejectUnsafeRoots(string fullPath)
    {
        var full = NormalizePath(fullPath);
        RejectUnsafePathCore(full);

        foreach (var resolved in ExpandResolvedPrefixes(full))
        {
            if (!string.Equals(resolved, full, StringComparison.OrdinalIgnoreCase))
            {
                RejectUnsafePathCore(resolved);
            }
        }
    }

    private static void RejectUnsafePathCore(string full)
    {
        // Drive / volume root: parent of "C:\Foo" is "C:\"; parent of "C:\" is null/same.
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent))
        {
            throw new ArgumentException($"Refusing workspace at drive root: {full}");
        }

        // Also catch "C:\" style where GetFullPath keeps trailing separator then trim emptied parent check.
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root)
                && string.Equals(
                    full.TrimEnd('\\', '/'),
                    root.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Refusing workspace at drive root: {full}");
            }
        }

        var forbidden = new List<string>();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var dir in new[] { win, system, programFiles, programFilesX86 })
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                forbidden.Add(NormalizePath(dir));
            }
        }

        foreach (var dir in forbidden)
        {
            if (OrbitPathNames.IsUnderOrEqual(full, dir))
            {
                throw new ArgumentException($"Refusing workspace under system directory: {full}");
            }
        }
    }

    /// <summary>
    /// Yield resolved paths after walking each segment. Example:
    /// <c>D:\ws\link\sub</c> where <c>link</c> → <c>C:\Windows</c> yields <c>C:\Windows</c>
    /// then <c>C:\Windows\sub</c>.
    /// </summary>
    internal static IEnumerable<string> ExpandResolvedPrefixes(string fullPath)
    {
        var full = NormalizePath(fullPath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            yield break;
        }

        var relative = full.Length > root.Length ? full[root.Length..] : "";
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = NormalizePath(root);
        foreach (var part in parts)
        {
            var next = NormalizePath(Path.Combine(current, part));
            // null = not a reparse point; throws if reparse exists but cannot be resolved (fail-closed).
            var resolved = TryResolveReparse(next) ?? next;
            if (!string.Equals(resolved, next, StringComparison.OrdinalIgnoreCase))
            {
                yield return resolved;
            }

            current = resolved;
        }

        if (!string.Equals(current, full, StringComparison.OrdinalIgnoreCase))
        {
            yield return current;
        }
    }

    /// <summary>
    /// Resolves a reparse point to its final target.
    /// Returns null when the path is not a reparse point.
    /// Throws when the path is a reparse point but the target cannot be resolved (fail-closed).
    /// </summary>
    private static string? TryResolveReparse(string path)
    {
        DirectoryInfo info;
        try
        {
            info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        FileAttributes attrs;
        try
        {
            attrs = info.Attributes;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Unable to inspect path for reparse points: {path}", ex);
        }

        if ((attrs & FileAttributes.ReparsePoint) == 0)
        {
            return null;
        }

        try
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                throw new ArgumentException($"Refusing workspace: unresolved reparse point at {path}");
            }

            return NormalizePath(target.FullName);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Refusing workspace: cannot resolve reparse point at {path}", ex);
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
