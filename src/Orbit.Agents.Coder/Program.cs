using Microsoft.Extensions.Logging;
using Orbit.Agents.Coder.Coder;
using Orbit.Agents.Hosting;

var agentId = Environment.GetEnvironmentVariable("ORBIT_AGENT_ID") ?? "coder";

// Workspace: ORBIT_WORKSPACE → ORBIT_CODER_ROOT → ORBIT_CODE_ROOT → ORBIT_ROOT → exe dir.
var workspace = FirstNonEmpty(
    Environment.GetEnvironmentVariable("ORBIT_WORKSPACE"),
    Environment.GetEnvironmentVariable("ORBIT_CODER_ROOT"),
    Environment.GetEnvironmentVariable("ORBIT_CODE_ROOT"),
    Orbit.Contracts.OrbitPathNames.TryGetOrbitRoot());

if (string.IsNullOrWhiteSpace(workspace))
{
    workspace = AppContext.BaseDirectory;
}

workspace = Path.GetFullPath(workspace.Trim());
if (!Directory.Exists(workspace))
{
    throw new DirectoryNotFoundException(
        $"workspace_missing: Coder start root does not exist: {workspace}");
}

Environment.SetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT", workspace);

var shell = new CoderShellTools(workspace);

var app = AgentHost.BuildAguiHost(
    args,
    agentId,
    configureServices: builder =>
    {
        builder.Services.AddSingleton(shell);
        builder.Services.AddSingleton(sp => CoderAgentFactory.Create(
            agentId,
            "Coding Assistant",
            """
            You are Orbit's Coding Assistant — a programming agent.

            Workspace root is dynamic per run: always call get_coder_workspace (or use FileAccess paths
            relative to that root). Do not assume the path printed at process start is authoritative
            when X-Orbit-Workspace is set for the session.

            Philosophy (from Mini Claude Code / 09 practice):
            Agent = tools + keep calling tools until the task is done. No magic.

            Loop: short think → use tools → report results.
            Rules:
            - Act first; do not only explain.
            - Never invent file paths; list/read when unsure.
            - Minimize changes; no over-engineering.
            - When finished, summarize what changed.
            """,
            shell,
            loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
    },
    healthExtras: _ => new Dictionary<string, string>
    {
        ["capability"] = "coder+file_access+bash",
        ["workspace"] = shell.WorkspaceRoot,
        ["shell"] = CoderShellTools.DescribePreferredShell()
    });

try
{
    await app.RunAsync().ConfigureAwait(false);
}
finally
{
    await Serilog.Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static string? FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
