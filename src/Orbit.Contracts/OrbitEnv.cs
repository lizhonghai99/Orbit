namespace Orbit.Contracts;

/// <summary>
/// Env lookup order: Process (Runtime-injected) → User → Machine.
/// Matches Agent ChatClientFactory after LLM settings sank to Runtime.
/// </summary>
public static class OrbitEnv
{
    public static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
}
