using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Shared agent wiring — hand-assembled Harness (not <c>AsHarnessAgent</c>).
/// Modules come from <c>ORBIT_HARNESS</c> / manifest <c>harness</c>
/// (default: mode + todo + skills + compaction + tool_approval).
/// </summary>
public static class AgentBootstrap
{
    public static AIAgent CreateAgent(
        string agentId,
        string displayName,
        string instructions,
        IList<AITool> tools,
        string? contentRoot = null,
        ILoggerFactory? loggerFactory = null,
        IEnumerable<AIAgent>? backgroundAgents = null,
        IReadOnlySet<string>? mcpToolNames = null,
        IReadOnlySet<string>? harness = null)
    {
        var enabled = new HashSet<string>(
            harness ?? HarnessProfile.ResolveEnabled(agentId: agentId),
            StringComparer.OrdinalIgnoreCase);
        var chat = ChatClientFactory.Create(agentId, loggerFactory);

        // Platform HITL: every agent gets ask_user_* (Shell cards). Do not re-declare per factory.
        var allTools = new List<AITool>(tools);
        allTools.AddRange(AskUserTools.CreateApprovalRequired());

        // ask_user_* (and many domain tools) are ApprovalRequired — without UseToolApproval they hang forever.
        if (!enabled.Contains(OrbitHarnessIds.ToolApproval))
        {
            loggerFactory?.CreateLogger(typeof(AgentBootstrap)).LogWarning(
                "Agent {AgentId}: harness omitted tool_approval but ApprovalRequired tools are present; enabling UseToolApproval automatically. Add \"tool_approval\" to agents/*.json harness.",
                agentId);
            enabled.Add(OrbitHarnessIds.ToolApproval);
        }

        // /health Describe must see the same set (incl. auto tool_approval), not re-parse ORBIT_HARNESS alone.
        AgentCapabilityHints.RegisterEffectiveHarness(agentId, enabled);

        var providers = new List<AIContextProvider>();

        if (enabled.Contains(OrbitHarnessIds.Mode))
        {
            providers.Add(new AgentModeProvider());
        }

        if (enabled.Contains(OrbitHarnessIds.Todo))
        {
            providers.Add(new TodoProvider());
        }

#pragma warning disable MAAI001 // FileSystemAgentFileStore / FileAccess experimental
        if (enabled.Contains(OrbitHarnessIds.FileMemory))
        {
            var memoryRoot = HarnessProfile.ResolveFileMemoryRoot(agentId, contentRoot);
            Directory.CreateDirectory(memoryRoot);
            providers.Add(new FileMemoryProvider(new FileSystemAgentFileStore(memoryRoot)));
        }

        if (enabled.Contains(OrbitHarnessIds.FileAccess))
        {
            // Ensure the resolved root exists so /health Enabled and /tools stay consistent
            // (root is re-resolved per call; missing → create, do not leave tools that always throw).
            var accessRoot = HarnessProfile.ResolveFileAccessRoot(contentRoot);
            Directory.CreateDirectory(accessRoot);
            providers.Add(new FileAccessProvider(new OrbitWorkspaceAgentFileStore(contentRoot)));
        }
#pragma warning restore MAAI001

        if (enabled.Contains(OrbitHarnessIds.Skills))
        {
            var skillsDir = SkillCatalog.ResolveSkillsDirectory(agentId, contentRoot);
            if (!string.IsNullOrWhiteSpace(skillsDir))
            {
                // Progressive disclosure: catalog in prompt; body via load_skill tool.
                providers.Add(new AgentSkillsProvider(skillsDir, loggerFactory: loggerFactory));
            }
        }

        // Optional: parent orchestrates named child agents (13ResearchWithSubAgents protocol).
        var children = backgroundAgents?.ToList() ?? [];
#pragma warning disable MAAI001 // BackgroundAgentsProvider experimental
        BackgroundAgentsProvider? backgroundProvider = null;
        if (enabled.Contains(OrbitHarnessIds.Background) && children.Count > 0)
        {
            backgroundProvider = new BackgroundAgentsProvider(children);
            providers.Add(backgroundProvider);
            AgentCapabilityHints.RegisterBackgroundAgents(
                agentId,
                children.Select(a => a.Name!).Where(n => !string.IsNullOrWhiteSpace(n)));
        }
#pragma warning restore MAAI001

        var modelId = ChatClientFactory.DescribeModel();
#pragma warning disable MEAI001 // InMemoryChatHistoryProviderOptions experimental surface
        var historyOptions = enabled.Contains(OrbitHarnessIds.Compaction)
            ? OrbitCompaction.CreateHistoryOptions(modelId)
            : new InMemoryChatHistoryProviderOptions();
        var options = new ChatClientAgentOptions
        {
            Name = agentId,
            Description = displayName,
            ChatOptions = new ChatOptions
            {
                Instructions = AppendHarnessHints(instructions, enabled),
                Tools = allTools,
                MaxOutputTokens = OrbitCompaction.ResolveMaxOutputTokens(),
                Reasoning = OrbitReasoning.ResolveOptions()
            },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(historyOptions),
            AIContextProviders = providers,
            // Pipeline already has logging / OTel / function invocation / per-call history (± compaction).
            UseProvidedChatClientAsIs = true,
            RequirePerServiceCallChatHistoryPersistence = true
        };
#pragma warning restore MEAI001

        var agent = chat.AsAIAgent(options, loggerFactory);
        AIAgent wired = agent;
        if (enabled.Contains(OrbitHarnessIds.ToolApproval))
        {
            // ToolApprovalAgent + official auto-approve rules for read-only Skills / FileAccess.
#pragma warning disable MAAI001 // FileAccessProvider.ReadOnlyToolsAutoApprovalRule experimental
            var autoRules = new List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>();
            if (enabled.Contains(OrbitHarnessIds.Skills))
            {
                autoRules.Add(AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule);
            }

            if (enabled.Contains(OrbitHarnessIds.FileAccess))
            {
                autoRules.Add(FileAccessProvider.ReadOnlyToolsAutoApprovalRule);
            }

            wired = agent.AsBuilder()
                .UseToolApproval(new ToolApprovalAgentOptions
                {
                    AutoApprovalRules = autoRules,
                })
                .Build();
#pragma warning restore MAAI001
        }

        // Tool schemas for Trajectory / session JSONL (GET /tools).
        // Domain + ask_user_* from ChatOptions; harness provider tools appended (Source=harness).
        AgentToolSchemaCatalog.Register(
            agentId,
            allTools,
            mcpToolNames,
            HarnessToolCatalog.ForEnabled(enabled, backgroundAgentsMounted: backgroundProvider is not null));

        if (backgroundProvider is not null)
        {
#pragma warning disable MAAI001
            return new BackgroundSessionReleasingAgent(wired, backgroundProvider);
#pragma warning restore MAAI001
        }

        return wired;
    }

    /// <summary>
    /// Append English-only platform hints that match the enabled Harness modules.
    /// Agent-specific tool guidance belongs in each agent's own instructions.
    /// </summary>
    private static string AppendHarnessHints(string instructions, IReadOnlySet<string> enabled)
    {
        var lines = new List<string> { "", "Harness:" };

        if (enabled.Contains(OrbitHarnessIds.Mode))
        {
            lines.Add(
                "- Prefer plan mode first for multi-step work; after the user approves the plan, switch to execute.");
        }

        if (enabled.Contains(OrbitHarnessIds.Todo))
        {
            lines.Add("- Use todos_* to track steps; complete them as tools succeed.");
        }

        if (enabled.Contains(OrbitHarnessIds.FileMemory))
        {
            lines.Add(
                "- file_memory_*: working notes that must survive compaction (lists, folder plans, MEMORY.md conventions).");
        }

        if (enabled.Contains(OrbitHarnessIds.FileAccess))
        {
            lines.Add(
                "- file_access_*: durable shared folder (reports/exports). ls/read/grep auto-run; write/delete/replace need user approval — call tools directly, do not only ask in plain text.");
        }

        if (enabled.Contains(OrbitHarnessIds.Skills))
        {
            lines.Add("- load_skill when a playbook matches; do not invent skill body text.");
        }

        if (enabled.Contains(OrbitHarnessIds.Background))
        {
            lines.Add(
                "- If BackgroundAgents are registered: start ALL tasks first, then wait/get_results/clear — never start→wait→start serially.");
        }

        // Platform HITL tools are always registered (ask_user_*).
        lines.Add(
            "- User interaction: for clarifications (A/B), direction picks, or plan/execute confirmation, call ask_user_choice or ask_user_confirm. Never ask the user to type option letters or approval words in plain chat — the Shell shows clickable cards.");

        return instructions + string.Join('\n', lines) + "\n";
    }
}
