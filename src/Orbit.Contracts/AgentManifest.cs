namespace Orbit.Contracts;

/// <summary>Static discovery record under agents/*.json.</summary>
public sealed class AgentManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    /// <summary>
    /// Path relative to repo root, e.g. <c>src/Orbit.Agents.Mail/Orbit.Agents.Mail.csproj</c>.
    /// Optional when <see cref="Command"/> is set (non-.NET / prebuilt exe).
    /// </summary>
    public string? Project { get; init; }
    /// <summary>
    /// Optional launch command: absolute path, or path relative to repo root.
    /// When omitted, Runtime resolves the built exe from <see cref="Project"/>.
    /// </summary>
    public string? Command { get; init; }
    /// <summary>
    /// Extra process environment. Applied before Runtime-owned keys
    /// (<c>ORBIT_ROOT</c>, token, harness, workspace, …) so host values win.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    /// <summary>Wire protocol exposed by the agent process.</summary>
    public string Protocol { get; init; } = "ag-ui";
    /// <summary>
    /// Default login URL opened by Playwright (QR / password in real window).
    /// Runtime injects this as <c>ORBIT_LOGIN_URL</c> for browser agents.
    /// </summary>
    public string? LoginUrl { get; init; }

    /// <summary>
    /// Host workspace policy: <c>required</c> | <c>optional</c> | <c>none</c>.
    /// Default <c>optional</c> when omitted from agents/*.json.
    /// </summary>
    public string Workspace { get; init; } = "optional";

    /// <summary>
    /// Harness modules passed to the agent as <c>ORBIT_HARNESS</c>:
    /// <c>mode</c>, <c>todo</c>, <c>skills</c>, <c>file_access</c>, <c>file_memory</c>,
    /// <c>background</c>, <c>compaction</c>, <c>tool_approval</c>.
    /// Empty/omitted → default <c>mode,todo,skills,compaction,tool_approval</c>
    /// (FileAccess / FileMemory / Background still require explicit opt-in).
    /// </summary>
    public IReadOnlyList<string> Harness { get; init; } = [];
}
