using Microsoft.Extensions.Logging;
using Orbit.Agents.Hosting;
using Orbit.Agents.Hosting.Mcp;
using Orbit.Agents.Research.Research;
using Serilog;

var agentId = Environment.GetEnvironmentVariable("ORBIT_AGENT_ID") ?? "research";

// File logging before MCP connect so early failures land in agent-research-*.log.
OrbitAgentLogging.Bootstrap(agentId);
using var earlyLog = LoggerFactory.Create(b => b.AddSerilog(Log.Logger, dispose: false));

// Reserve domain tool names so MCP aliases cannot collide (toolPrefix "" still safe).
string[] reservedDomainTools = ["run_research_pipeline"];

var mcp = await McpToolHost.LoadAsync(
        agentId,
        loggerFactory: earlyLog,
        reservedToolNames: reservedDomainTools,
        CancellationToken.None)
    .ConfigureAwait(false);
var mcpHost = McpToolHost.TryGet(agentId);

var app = AgentHost.BuildAguiHost(
    args,
    agentId,
    configureServices: builder =>
    {
        var pipeline = new ResearchPipeline(agentId, contentRoot: null, ResearchStageReporter.ReportAsync);
        builder.Services.AddSingleton(pipeline);
        if (mcpHost is not null)
        {
            builder.Services.AddSingleton(mcpHost);
        }

        builder.Services.AddSingleton(sp => ResearchAgentFactory.Create(
            agentId,
            "Research Assistant",
            """
            You are Orbit's Research Assistant (parent orchestrator).

            Two pillars — choose by user intent (see tool policy):
            1) BackgroundAgents — concurrent AngleResearcher fact-finding (Practice 13).
            2) Workflow — run_research_pipeline Scope→Outline→Draft (pass evidence when you have it).

            Prefer one path; combo only when the user wants both probes and a polished brief.
            For live facts use MCP search (parent or children). Never invent sources.
            """,
            pipeline,
            extraTools: mcp.Tools,
            loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
    },
    healthExtras: sp =>
    {
        var pipeline = sp.GetRequiredService<ResearchPipeline>();
        var bg = AgentCapabilityHints.GetBackgroundAgents(agentId);
        var host = sp.GetService<McpToolHost>() ?? mcpHost;
        return new Dictionary<string, string>
        {
            ["capability"] = "workflow+background_agents+research+mcp",
            ["pipeline"] = "Scope→Outline→Draft",
            ["checkpointRoot"] = pipeline.CheckpointRoot,
            ["backgroundAgents"] = bg is null ? "" : string.Join(",", bg),
            ["mcpConfigured"] = (host?.Configured ?? mcp.Configured) ? "true" : "false",
            ["mcpServers"] = string.Join(",", (host?.Servers ?? mcp.Servers).Select(s => $"{s.Id}:{s.Status}"))
        };
    });

app.Lifetime.ApplicationStopping.Register(() =>
{
    mcpHost?.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

try
{
    await app.RunAsync().ConfigureAwait(false);
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
