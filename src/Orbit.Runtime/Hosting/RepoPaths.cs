namespace Orbit.Runtime.Hosting;

public sealed class RepoPaths
{
    public required string RepoRoot { get; init; }
    public string AgentsDir => Path.Combine(RepoRoot, "agents");
    public string DataDir => Path.Combine(RepoRoot, "data");
    public string SessionsDir => Path.Combine(DataDir, "sessions");
    public string ProfilesDir => Path.Combine(DataDir, "profiles");

    public static RepoPaths Resolve()
    {
        var fromEnv = Orbit.Contracts.OrbitPathNames.TryGetOrbitRoot();
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(Path.Combine(fromEnv, "agents")))
        {
            return Ensure(new RepoPaths { RepoRoot = Path.GetFullPath(fromEnv) });
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            var slnx = Path.Combine(dir.FullName, "Orbit.slnx");
            if (Directory.Exists(agents) && File.Exists(slnx))
            {
                return Ensure(new RepoPaths { RepoRoot = dir.FullName });
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Cannot locate Orbit repo root (folder with Orbit.slnx + agents/). Set ORBIT_ROOT.");
    }

    private static RepoPaths Ensure(RepoPaths paths)
    {
        Directory.CreateDirectory(paths.DataDir);
        Directory.CreateDirectory(paths.SessionsDir);
        Directory.CreateDirectory(paths.ProfilesDir);
        return paths;
    }
}
