using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orbit.Agents.Hosting;

namespace Orbit.Agents.Coder.Coder;

public static class CoderAgentFactory
{
    public static AIAgent Create(
        string agentId,
        string displayName,
        string instructions,
        CoderShellTools shell,
        ILoggerFactory? loggerFactory = null)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => $"Coder workspace: {shell.WorkspaceRoot}",
                name: "get_coder_workspace",
                description: "Return the on-disk workspace root for this run (FileAccess + shell cwd)."),
            // Shell can mutate the machine — HITL like 09 bash, but Orbit requires approval.
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                shell.RunBashAsync,
                name: "bash",
                description:
                "Run a shell command in the Coder workspace (Git Bash required; set ORBIT_BASH_PATH to override). "
                + "Use for ls/find/grep/git/dotnet/tests. 60s timeout. Requires user approval.")),
        };

        return AgentBootstrap.CreateAgent(
            agentId,
            displayName,
            instructions + $"""

                Workspace: call get_coder_workspace each turn (per-run via X-Orbit-Workspace when set).

                Tool map (Harness + Orbit):
                - Explore: file_access_ls / file_access_read / file_access_grep (auto-approved reads).
                - Edit: file_access_write / replace / delete — call tools; need user approval (HITL).
                - Shell: bash — also needs approval; prefer FileAccess for file I/O, bash for build/test/git.
                - Notes: file_memory_* for working notes that must survive compaction (not a substitute for repo files).
                - Plan: mode_set plan → todos_* → mode_set execute for multi-step work.
                - Skills: load_skill when a playbook matches; do not invent skill body text.

                Loop: brief think → tools → (update todos) → report what changed.
                Do not invent paths; if unsure, list/read first. Prefer minimal diffs.
                Reply in the user's language.
                """,
            tools,
            loggerFactory: loggerFactory);
    }
}
