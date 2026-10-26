namespace Orbit.Agents.Hosting;

/// <summary>
/// Ambient run identity + workspace for FunctionInvoker telemetry and tool roots.
/// Set from <c>X-Orbit-*</c> headers on inbound Agent HTTP.
/// </summary>
public static class OrbitRunContext
{
    private static readonly AsyncLocal<OrbitRunScope?> Current = new();

    public static string? RunId => Current.Value?.RunId;
    public static string? SessionId => Current.Value?.SessionId;
    public static string? Workspace => Current.Value?.Workspace;
    public static string? WorkspaceId => Current.Value?.WorkspaceId;

    public static IDisposable Push(
        string? runId,
        string? sessionId,
        string? workspace = null,
        string? workspaceId = null)
    {
        var previous = Current.Value;
        Current.Value = new OrbitRunScope(runId, sessionId, workspace, workspaceId);
        return new PopScope(previous);
    }

    private sealed record OrbitRunScope(
        string? RunId,
        string? SessionId,
        string? Workspace,
        string? WorkspaceId);

    private sealed class PopScope(OrbitRunScope? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
