using Microsoft.Extensions.Logging;
using Orbit.Agents.Coder.Coder;
using Orbit.Agents.Hosting;

var agentId = Environment.GetEnvironmentVariable("ORBIT_AGENT_ID") ?? "coder";

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
        builder.Services.AddSingleton(sp =>
            new FeatureListWorkflow(
                agentId,
                contentRoot: null,
                chatFactory: null,
                loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton(sp => CoderAgentFactory.Create(
            agentId,
            "Coding Assistant",
            shell,
            sp.GetRequiredService<FeatureListWorkflow>(),
            loggerFactory: sp.GetRequiredService<ILoggerFactory>(),
            contentRoot: builder.Environment.ContentRootPath));
    },
    healthExtras: sp =>
    {
        var featureList = sp.GetRequiredService<FeatureListWorkflow>();
        return new Dictionary<string, string>
        {
            ["capability"] = "handoff+Short-Running|Long-Running+context-reset",
            ["router"] = CoderAgentFactory.RouterId,
            ["planner"] = CoderAgentFactory.PlannerId,
            ["generator"] = CoderAgentFactory.GeneratorId,
            ["evaluator"] = CoderAgentFactory.EvaluatorId,
            ["shortRunning"] = "router→generator→evaluator (HITL)",
            ["longRunning"] = "router→planner; run_coding_turn=fresh Generator+Evaluator sessions",
            ["featureList"] = "feature_list.plan→release_ready; QA in run_coding_turn",
            ["featureListRoot"] = featureList.StoreRootHint,
            ["workspace"] = shell.WorkspaceRoot,
            ["shell"] = CoderShellTools.DescribePreferredShell()
        };
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
