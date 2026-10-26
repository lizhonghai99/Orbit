using Microsoft.Agents.AI;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Names for tools injected by AIContextProviders (not on ChatOptions.Tools).
/// Prefer MAF <c>*ToolName</c> constants; where none exist, keep a golden list locked by tests.
/// </summary>
public static class HarnessToolCatalog
{
    /// <summary>
    /// TodoProvider has no public *ToolName fields — names come from its DefaultInstructions (MAF 1.26).
    /// </summary>
    public static readonly string[] TodoToolNames =
    [
        "todos_add",
        "todos_complete",
        "todos_get_all",
        "todos_get_remaining",
        "todos_remove"
    ];

    /// <summary>AgentModeProvider tool surface (MAF 1.26).</summary>
    public static readonly string[] ModeToolNames =
    [
        "mode_set",
        "mode_get",
        "mode_get_instructions",
        "mode_set_instructions",
        "mode_transition"
    ];

    /// <summary>BackgroundAgentsProvider tool surface (MAF 1.26).</summary>
    public static readonly string[] BackgroundToolNames =
    [
        "background_agents_start_task",
        "background_agents_wait_for_first_completion",
        "background_agents_get_task_results",
        "background_agents_get_all_tasks",
        "background_agents_clear_completed_task",
        "background_agents_continue_task"
    ];

    /// <param name="backgroundAgentsMounted">
    /// True only when BackgroundAgentsProvider was actually constructed (children present).
    /// </param>
    public static IReadOnlyList<ToolSchemaDto> ForEnabled(
        IReadOnlySet<string> enabled,
        bool backgroundAgentsMounted = false)
    {
        var list = new List<ToolSchemaDto>();

        if (enabled.Contains(OrbitHarnessIds.Mode))
        {
            foreach (var name in ModeToolNames)
            {
                list.Add(Stub(name, "Agent mode harness tool.", requiresApproval: false));
            }
        }

        if (enabled.Contains(OrbitHarnessIds.Todo))
        {
            foreach (var name in TodoToolNames)
            {
                list.Add(Stub(name, "Todo harness tool.", requiresApproval: false));
            }
        }

        if (enabled.Contains(OrbitHarnessIds.Skills))
        {
#pragma warning disable MAAI001
            list.Add(Stub(AgentSkillsProvider.LoadSkillToolName, "Load a skill body by name.", requiresApproval: false));
            list.Add(Stub(AgentSkillsProvider.ReadSkillResourceToolName, "Read a skill resource file.", requiresApproval: false));
            list.Add(Stub(AgentSkillsProvider.RunSkillScriptToolName, "Run a skill script (may need approval).", requiresApproval: true));
#pragma warning restore MAAI001
        }

        if (enabled.Contains(OrbitHarnessIds.FileAccess))
        {
#pragma warning disable MAAI001 // FileAccessProvider experimental — names must track *ToolName fields.
            list.Add(Stub(FileAccessProvider.LsToolName, "List directory under workspace file-access root.", requiresApproval: false));
            list.Add(Stub(FileAccessProvider.ReadFileToolName, "Read a file under workspace file-access root.", requiresApproval: false));
            list.Add(Stub(FileAccessProvider.ReadLinesToolName, "Read a line range from a file.", requiresApproval: false));
            list.Add(Stub(FileAccessProvider.GrepToolName, "Search files under workspace file-access root.", requiresApproval: false));
            list.Add(Stub(FileAccessProvider.WriteToolName, "Write a file (HITL).", requiresApproval: true));
            list.Add(Stub(FileAccessProvider.ReplaceToolName, "Replace text in a file (HITL).", requiresApproval: true));
            list.Add(Stub(FileAccessProvider.ReplaceLinesToolName, "Replace whole lines in a file (HITL).", requiresApproval: true));
            list.Add(Stub(FileAccessProvider.DeleteFileToolName, "Delete a file (HITL).", requiresApproval: true));
#pragma warning restore MAAI001
        }

        if (enabled.Contains(OrbitHarnessIds.FileMemory))
        {
#pragma warning disable MAAI001
            list.Add(Stub(FileMemoryProvider.LsToolName, "List working-memory notes.", requiresApproval: false));
            list.Add(Stub(FileMemoryProvider.ReadFileToolName, "Read working-memory notes.", requiresApproval: false));
            list.Add(Stub(FileMemoryProvider.GrepToolName, "Search working-memory notes.", requiresApproval: false));
            list.Add(Stub(FileMemoryProvider.WriteToolName, "Write working-memory notes.", requiresApproval: false));
            list.Add(Stub(FileMemoryProvider.ReplaceToolName, "Replace text in a memory file.", requiresApproval: false));
            list.Add(Stub(FileMemoryProvider.ReplaceLinesToolName, "Replace lines in a memory file.", requiresApproval: false));
            list.Add(Stub(FileMemoryProvider.DeleteFileToolName, "Delete a memory file.", requiresApproval: false));
#pragma warning restore MAAI001
        }

        if (enabled.Contains(OrbitHarnessIds.Background) && backgroundAgentsMounted)
        {
            foreach (var name in BackgroundToolNames)
            {
                list.Add(Stub(name, "Background agents harness tool.", requiresApproval: false));
            }
        }

        return list;
    }

    private static ToolSchemaDto Stub(string name, string description, bool requiresApproval) =>
        new()
        {
            Name = name,
            Description = description,
            Parameters = null,
            RequiresApproval = requiresApproval,
            Source = "harness"
        };
}
