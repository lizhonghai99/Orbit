using Orbit.Runtime.Agents;
using Xunit;

namespace Orbit.Runtime.Tests;

public class AgentSupervisorOrphanTests
{
    [Fact]
    public void IsOrphanAgentCandidate_requires_orbit_agent_name_under_repo()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-orphan-root");
        var under = Path.Combine(root, "src", "Orbit.Agents.Mail", "bin", "Mail.exe");
        var outside = Path.Combine(Path.GetTempPath(), "other", "Orbit.Agents.Mail.exe");

        Assert.True(AgentSupervisor.IsOrphanAgentCandidate("Orbit.Agents.Mail", under, root));
        Assert.False(AgentSupervisor.IsOrphanAgentCandidate("Orbit.Agents.Mail", outside, root));
        Assert.False(AgentSupervisor.IsOrphanAgentCandidate("notepad", under, root));
        Assert.False(AgentSupervisor.IsOrphanAgentCandidate("Orbit.Agents.Mail", null, root));
        Assert.False(AgentSupervisor.IsOrphanAgentCandidate("Orbit.Agents.Mail", "   ", root));
    }
}
