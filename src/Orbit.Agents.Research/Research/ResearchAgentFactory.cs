using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orbit.Agents.Hosting;
using Orbit.Agents.Hosting.Mcp;

namespace Orbit.Agents.Research.Research;

public static class ResearchAgentFactory
{
    /// <summary>
    /// Parent Research agent: <see cref="BackgroundAgentsProvider"/> (13) + Workflow tool (Scope→Outline→Draft).
    /// </summary>
    public static AIAgent Create(
        string agentId,
        string displayName,
        string instructions,
        ResearchPipeline pipeline,
        IReadOnlyList<AITool>? extraTools = null,
        ILoggerFactory? loggerFactory = null)
    {
        var mcpTools = extraTools ?? [];
        var angleResearcher = CreateAngleResearcher(mcpTools, loggerFactory);

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                RunPipelineTool,
                name: "run_research_pipeline",
                description:
                "Run Scope→Outline→Draft Workflow with checkpoints. "
                + "Pass evidence= condensed notes/URLs from AngleResearcher and/or your MCP searches "
                + "so Draft does not invent citations. Omit evidence only for a thin outline-only pass. "
                + "Set resume=true to continue from the latest on-disk checkpoint for this pipeline session."),
        };

        Task<string> RunPipelineTool(
            string topic,
            string? focus = null,
            string? evidence = null,
            bool resume = false,
            CancellationToken ct = default) =>
            pipeline.RunAsync(topic, focus, evidence, resume, ct);
        tools.AddRange(mcpTools);
        var mcpNames = mcpTools.Count == 0 ? null : McpToolHost.CollectToolNames(mcpTools);

        return AgentBootstrap.CreateAgent(
            agentId,
            displayName,
            instructions + """

                You have TWO research tools — pick by intent (do not always run both):

                Path A — BackgroundAgents (concurrent AngleResearcher):
                  Use when the user wants multi-angle fact-finding / comparison / "最新进展".
                  Protocol (strict):
                  1. Clarification first if the topic is ambiguous → ask_user_choice (one question).
                  2. Plan briefly (mode plan + todos) → ask_user_confirm → mode execute.
                  3. background_agents_start_task(agentName=AngleResearcher, …) for EACH angle — START ALL FIRST.
                  4. Loop wait_for_first_completion → get_task_results until done; optional continue_task if thin.
                  5. clear_completed_task. Summarize with a Markdown table; cite URLs from tool results.
                  Never start→wait→start serially. Never invent facts/citations.
                  If several child tasks fail with infra errors: say so, fall back to your own MCP searches, do not pretend A succeeded.

                Path B — Workflow (run_research_pipeline):
                  Use when the user wants a structured brief / 简报 / write-up.
                  ALWAYS pass evidence= (notes + URLs) from A and/or your MCP searches when you have them.
                  Without evidence, pipeline will only produce an unverified scaffold — tell the user that.
                  If a previous B run was interrupted, call with resume=true once.

                Combo (optional, not default):
                  A (or parent MCP probes) → then B with evidence= the gathered notes.
                  Do not call B empty "just because it exists".

                Parent may also call MCP search tools directly for quick checks or A-fallback.
                Save lasting notes with file_memory_*; exports with file_access_* (writes need approval).
                User-facing replies: Markdown. Match the user's language.
                """,
            tools,
            backgroundAgents: [angleResearcher],
            mcpToolNames: mcpNames,
            loggerFactory: loggerFactory);
    }

    /// <summary>
    /// Leaf worker: no Harness providers — parent owns orchestration (13 pattern).
    /// MCP tools + ToolApproval so ApprovalRequired MCP mutations surface HITL.
    /// </summary>
    private static AIAgent CreateAngleResearcher(
        IReadOnlyList<AITool> mcpTools,
        ILoggerFactory? loggerFactory)
    {
        var chat = ChatClientFactory.Create("research-angle", loggerFactory);
        var leaf = new ChatClientAgent(
            chat,
            instructions:
            """
            You are AngleResearcher — a focused research sub-agent.
            Handle exactly ONE angle, sub-question, or ticker from the parent prompt.
            For live facts, call the MCP search tools (e.g. tavily_search). Cite URLs from results.
            If search fails, say so; never invent sources or URLs.
            Return concise Markdown: findings, uncertainties, and what would need primary sources.
            Do not orchestrate other agents; the parent owns BackgroundAgents scheduling.
            """,
            name: "AngleResearcher",
            tools: mcpTools.Count == 0 ? null : mcpTools.ToList());

        // Leaf / background agents must not UseToolApproval — nobody is there to click cards.
        // Parent Research owns HITL; MCP tools for AngleResearcher should stay requiresApproval=false.
        return leaf;
    }
}
