using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Agui;

namespace Orbit.Runtime.Workspaces;

/// <summary>
/// After host workspace path changes: cancel in-flight runs then RestartAsync affected agents.
/// </summary>
public sealed class WorkspaceAgentRestarter(
    AgentRegistry registry,
    IAgentSupervisor supervisor,
    AguiSessionRunner runs,
    ILogger<WorkspaceAgentRestarter> logger)
{
    public void EnsureConfirmOrThrow(string? newActivePath, bool confirm)
    {
        var needs = AgentsNeedingRestart(newActivePath);
        if (needs.Count == 0)
        {
            return;
        }

        var activeRuns = runs.CountActiveRuns(needs);
        if (activeRuns <= 0 || confirm)
        {
            return;
        }

        throw new OrbitApiException(
            OrbitErrorCodes.ConfirmRequired,
            $"{activeRuns} active run(s) will be cancelled; agents={string.Join(",", needs)}",
            new Dictionary<string, string>
            {
                ["activeRuns"] = activeRuns.ToString(),
                ["agents"] = string.Join(",", needs)
            });
    }

    public async Task<WorkspaceMutationResult> AfterMutationAsync(
        WorkspaceRecord? active,
        CancellationToken cancellationToken = default)
    {
        var targetPath = active?.Path;
        var needs = AgentsNeedingRestart(targetPath);
        var result = new WorkspaceMutationResult
        {
            Active = active,
            RequiresRestartAgents = needs.ToList()
        };

        foreach (var agentId in needs)
        {
            try
            {
                var cancelled = runs.CancelAllForAgent(agentId);
                if (cancelled > 0)
                {
                    logger.LogInformation(
                        "Cancelled {Count} in-flight run(s) for {AgentId} before workspace restart",
                        cancelled,
                        agentId);
                }

                await supervisor.RestartAsync(agentId, cancellationToken).ConfigureAwait(false);
                result.RestartedAgents.Add(agentId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to restart {AgentId} after workspace change", agentId);
                result.RestartFailures[agentId] = ex.Message;
            }
        }

        return result;
    }

    public List<string> AgentsNeedingRestart(string? newActivePath)
    {
        var ids = new List<string>();
        foreach (var info in registry.List())
        {
            if (info.Lifecycle != AgentLifecycle.Running)
            {
                continue;
            }

            if (!registry.TryGet(info.Id, out var tracked))
            {
                continue;
            }

            if (OrbitWorkspacePolicy.IsNone(tracked.Manifest.Workspace))
            {
                continue;
            }

            if (AguiSessionRunner.WorkspacePathEquals(tracked.StartedWorkspacePath, newActivePath))
            {
                continue;
            }

            ids.Add(info.Id);
        }

        return ids;
    }
}
