using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Agui;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Orbit.Runtime.Workspaces;
using Xunit;

namespace Orbit.Runtime.Tests;

public class WorkspaceAgentRestarterTests : IDisposable
{
    private readonly string _root;
    private readonly AgentRegistry _registry;
    private readonly WorkspaceAgentRestarter _sut;

    public WorkspaceAgentRestarterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orbit-ws-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "agents"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "sessions"));
        File.WriteAllText(
            Path.Combine(_root, "agents", "coder.agent.json"),
            """
            {
              "id": "coder",
              "displayName": "x",
              "project": "src/Orbit.Agents.Coder/Orbit.Agents.Coder.csproj",
              "protocol": "ag-ui",
              "workspace": "required"
            }
            """);
        File.WriteAllText(
            Path.Combine(_root, "agents", "mail.agent.json"),
            """
            {
              "id": "mail",
              "displayName": "x",
              "project": "src/Orbit.Agents.Mail/Orbit.Agents.Mail.csproj",
              "protocol": "ag-ui",
              "workspace": "none"
            }
            """);

        var paths = new RepoPaths { RepoRoot = _root };
        _registry = new AgentRegistry(paths);
        // Minimal runner — CancelAllForAgent only needs empty active map.
        var sessions = new JsonlSessionStore(paths);
        var runner = new AguiSessionRunner(
            _registry,
            sessions,
            null!,
            null!,
            null!,
            new OrbitRuntimeAuth(),
            null!,
            NullLogger<AguiSessionRunner>.Instance);
        _sut = new WorkspaceAgentRestarter(
            _registry,
            new NoOpSupervisor(),
            runner,
            NullLogger<WorkspaceAgentRestarter>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void AgentsNeedingRestart_skips_same_path_and_mail()
    {
        Assert.True(_registry.TryGet("coder", out var coder));
        Assert.True(_registry.TryGet("mail", out var mail));
        coder.Lifecycle = AgentLifecycle.Running;
        coder.StartedWorkspacePath = @"D:\ws-a";
        mail.Lifecycle = AgentLifecycle.Running;
        mail.StartedWorkspacePath = null;

        var same = _sut.AgentsNeedingRestart(@"D:\ws-a");
        Assert.Empty(same);

        var changed = _sut.AgentsNeedingRestart(@"D:\ws-b");
        Assert.Equal(["coder"], changed);

        var cleared = _sut.AgentsNeedingRestart(null);
        Assert.Equal(["coder"], cleared);
    }

    private sealed class NoOpSupervisor : IAgentSupervisor
    {
        public int CleanupOrphanAgentProcesses() => 0;
        public Task StopAllAsync() => Task.CompletedTask;
        public Task<AgentInfo> StartAsync(string agentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<AgentInfo> StopAsync(string agentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<AgentInfo> RestartAsync(string agentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReloadRunningAgentsResult> RestartRunningAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ReloadRunningAgentsResult());
    }
}
