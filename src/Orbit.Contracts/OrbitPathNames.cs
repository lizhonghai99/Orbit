namespace Orbit.Contracts;

/// <summary>Shared path helpers — agent id sanitization and <c>ORBIT_ROOT</c> resolution.</summary>
public static class OrbitPathNames
{
    public static string? TryGetOrbitRoot()
    {
        var root = Environment.GetEnvironmentVariable("ORBIT_ROOT");
        return string.IsNullOrWhiteSpace(root) ? null : root.Trim();
    }

    /// <summary>
    /// Prefer <c>ORBIT_ROOT</c>, else <paramref name="contentRootFallback"/>, else <see cref="AppContext.BaseDirectory"/>.
    /// Env wins on purpose for agent processes (host injects <c>ORBIT_ROOT</c>); the parameter is only a fallback.
    /// Call sites that must honor an explicit directory should not use this helper.
    /// </summary>
    public static string ResolveDataRoot(string? contentRootFallback = null)
    {
        var fromEnv = TryGetOrbitRoot();
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv!;
        }

        return string.IsNullOrWhiteSpace(contentRootFallback)
            ? AppContext.BaseDirectory
            : contentRootFallback;
    }

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        // Collapse path traversal / separators that InvalidFileNameChars may miss on some OS.
        name = name.Replace("..", "_", StringComparison.Ordinal)
            .Replace('/', '_')
            .Replace('\\', '_');

        return string.IsNullOrWhiteSpace(name) ? "_" : name.Trim();
    }

    /// <summary>
    /// True when <paramref name="path"/> is strictly inside <paramref name="root"/> (not equal to it).
    /// Segment-aware: <c>D:\Code\Orbit2</c> is NOT under <c>D:\Code\Orbit</c>.
    /// Windows comparisons are case-insensitive; other OSes are case-sensitive.
    /// </summary>
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string full;
        string fullRoot;
        try
        {
            full = Path.GetFullPath(path);
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // Keep a trailing separator on the root so "C:\" and "D:\Code\Orbit" share one branch.
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return full.Length > prefix.Length
               && full.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// True when <paramref name="path"/> equals <paramref name="root"/> or is under it
    /// (same segment rules as <see cref="IsUnder"/>).
    /// </summary>
    public static bool IsUnderOrEqual(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string full;
        string fullRoot;
        try
        {
            full = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(full, fullRoot, comparison) || IsUnder(path, root);
    }

    /// <summary>
    /// Agent ids used in paths: lowercase alphanumeric + hyphen/underscore only (manifest-controlled).
    /// </summary>
    public static bool IsSafeAgentId(string? agentId) =>
        !string.IsNullOrWhiteSpace(agentId)
        && agentId.Length <= 64
        && agentId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
