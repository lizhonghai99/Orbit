namespace Orbit.Contracts;

/// <summary>
/// Process-scoped Runtime secrets.
/// <see cref="ShellToken"/> is persisted for Shell HTTP; <see cref="AgentToken"/> stays in-memory
/// and is injected into Agent processes — Shell never receives it.
/// </summary>
public sealed class OrbitRuntimeAuth
{
    /// <summary>Shell ↔ Runtime. Written to <c>runtime.token</c> after bind succeeds.</summary>
    public string? ShellToken { get; set; }

    /// <summary>
    /// Runtime ↔ Agent only. Never persisted; never given to Shell.
    /// Accepted solely on <c>POST /sessions/{id}/agent-meta</c> (research_stage etc.);
    /// all other Runtime HTTP routes still require <see cref="ShellToken"/>.
    /// </summary>
    public string? AgentToken { get; set; }
}
