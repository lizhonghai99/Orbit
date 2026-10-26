using Orbit.Agents.Hosting;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class CapabilityProfileToolDisableTests
{
    [Fact]
    public void IsToolDisabled_blocks_named_tool()
    {
        var profile = CapabilityProfile.Instance;
        profile.Apply(new CapabilityPatchRequest
        {
            DisabledTools = ["tavily_search", "run_research_pipeline"]
        });
        try
        {
            Assert.True(profile.IsToolDisabled("tavily_search"));
            Assert.True(profile.IsToolDisabled("run_research_pipeline"));
            Assert.False(profile.IsToolDisabled("other_tool"));
        }
        finally
        {
            profile.Apply(new CapabilityPatchRequest { DisabledTools = [] });
        }
    }
}
