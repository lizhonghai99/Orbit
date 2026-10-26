using Orbit.Contracts;

namespace Orbit.Runtime.Agents;

/// <summary>
/// Agent process lifecycle seam — local Process today, container/remote later.
/// </summary>
public interface IAgentSupervisor
{
    int CleanupOrphanAgentProcesses();
    Task StopAllAsync();
    Task<AgentInfo> StartAsync(string agentId, CancellationToken cancellationToken);
    Task<AgentInfo> StopAsync(string agentId, CancellationToken cancellationToken);
    Task<AgentInfo> RestartAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>Stop+start every agent currently <see cref="AgentLifecycle.Running"/> (e.g. after LLM change).</summary>
    Task<ReloadRunningAgentsResult> RestartRunningAsync(CancellationToken cancellationToken);
}
