namespace Orbit.Agents.Hosting;

/// <summary>
/// Resolve tool roots: <see cref="OrbitRunContext.Workspace"/> → process env → fallbacks.
/// </summary>
public static class OrbitWorkspaceRoots
{
    public static string? TryAmbientWorkspace()
    {
        try
        {
            var fromCtx = OrbitRunContext.Workspace;
            if (!string.IsNullOrWhiteSpace(fromCtx))
            {
                return Path.GetFullPath(fromCtx.Trim());
            }

            var fromEnv = FirstNonEmpty(
                Environment.GetEnvironmentVariable("ORBIT_WORKSPACE"),
                Environment.GetEnvironmentVariable("ORBIT_CODER_ROOT"),
                Environment.GetEnvironmentVariable("ORBIT_CODE_ROOT"));
            return string.IsNullOrWhiteSpace(fromEnv) ? null : Path.GetFullPath(fromEnv.Trim());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>FileAccess root for the current run (or process default under contentRoot).</summary>
    public static string ResolveFileAccessRoot(string? contentRoot)
    {
        var ambient = TryAmbientWorkspace();
        if (!string.IsNullOrWhiteSpace(ambient))
        {
            return ambient;
        }

        var fileAccessEnv = Environment.GetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT")
                            ?? Environment.GetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(fileAccessEnv))
        {
            return Path.GetFullPath(fileAccessEnv.Trim());
        }

        return HarnessProfile.ResolveFileAccessRoot(contentRoot);
    }

    /// <summary>Shell/bash cwd: ambient workspace or process ctor fallback.</summary>
    public static string ResolveShellRoot(string processFallback)
    {
        var ambient = TryAmbientWorkspace();
        if (!string.IsNullOrWhiteSpace(ambient))
        {
            return ambient;
        }

        return Path.GetFullPath(processFallback);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }
        }

        return null;
    }
}
