using System.Diagnostics;
using System.Net.Http;

namespace Orbit.Shell.Services;

public static class RuntimeLauncher
{
    /// <summary>
    /// Ensure a Runtime is reachable: reuse a healthy one, otherwise spawn.
    /// Prefer <see cref="TryStartAsync"/> from UI / async startup paths.
    /// </summary>
    public static bool TryStart() =>
        TryStartAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    public static async Task<bool> TryStartAsync(CancellationToken cancellationToken = default)
    {
        if (await IsRuntimeHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var exe = FindRuntimeExe();
        if (exe is null)
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return true;
    }

    public static bool IsRuntimeHealthy() =>
        IsRuntimeHealthyAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    public static async Task<bool> IsRuntimeHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // /health is intentionally unauthenticated so we can probe before attaching X-Orbit-Token.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await http
                .GetAsync("http://127.0.0.1:5050/health", cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orbit.slnx")) &&
                Directory.Exists(Path.Combine(dir.FullName, "agents")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Orbit.Contracts.OrbitPathNames.TryGetOrbitRoot();
    }

    private static string? FindRuntimeExe()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(
                root, "src", "Orbit.Runtime", "bin", config, "net10.0", "Orbit.Runtime.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
