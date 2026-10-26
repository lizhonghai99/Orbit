using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class OrbitLocalAuthGateTests
{
    [Fact]
    public void Health_get_is_exempt()
    {
        Assert.True(OrbitLocalAuthGate.IsHealthExempt("GET", "/health"));
        Assert.True(OrbitLocalAuthGate.IsHealthExempt("get", "/health"));
        Assert.False(OrbitLocalAuthGate.IsHealthExempt("POST", "/health"));
        Assert.False(OrbitLocalAuthGate.IsHealthExempt("GET", "/agents"));
    }

    [Fact]
    public void Authorize_requires_exact_token()
    {
        Assert.True(OrbitLocalAuthGate.IsAuthorized("abc", "abc"));
        Assert.False(OrbitLocalAuthGate.IsAuthorized("abc", "xyz"));
        Assert.False(OrbitLocalAuthGate.IsAuthorized(null, "abc"));
        Assert.False(OrbitLocalAuthGate.IsAuthorized("abc", ""));
    }

    [Fact]
    public void Authorize_trims_presented()
    {
        Assert.True(OrbitLocalAuthGate.IsAuthorized("  secret  ", "secret"));
    }

    [Fact]
    public void Agent_meta_path_is_narrow()
    {
        Assert.True(OrbitLocalAuthGate.IsAgentMetaPath("POST", "/sessions/abc/agent-meta"));
        Assert.False(OrbitLocalAuthGate.IsAgentMetaPath("GET", "/sessions/abc/agent-meta"));
        Assert.False(OrbitLocalAuthGate.IsAgentMetaPath("POST", "/sessions/abc/entries"));
        Assert.False(OrbitLocalAuthGate.IsAgentMetaPath("POST", "/sessions/abc/events"));
    }

    [Fact]
    public void LogsDirectory_is_under_TokenDirectory()
    {
        Assert.Equal(
            Path.Combine(OrbitLocalAuth.TokenDirectory, "logs"),
            OrbitLocalAuth.LogsDirectory);
    }
}
