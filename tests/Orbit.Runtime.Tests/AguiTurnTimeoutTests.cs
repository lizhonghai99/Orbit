using Orbit.Runtime.Agui;
using Xunit;

namespace Orbit.Runtime.Tests;

[Collection("orbit-env")]
public class AguiTurnTimeoutTests
{
    [Fact]
    public void ResolveTurnHttpTimeout_defaults_to_infinite()
    {
        var prev = Environment.GetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES", null);
            Assert.Equal(Timeout.InfiniteTimeSpan, AguiSessionRunner.ResolveTurnHttpTimeout());

            Environment.SetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES", "0");
            Assert.Equal(Timeout.InfiniteTimeSpan, AguiSessionRunner.ResolveTurnHttpTimeout());

            Environment.SetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES", "nope");
            Assert.Equal(Timeout.InfiniteTimeSpan, AguiSessionRunner.ResolveTurnHttpTimeout());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES", prev);
        }
    }

    [Fact]
    public void ResolveTurnHttpTimeout_honors_positive_minutes()
    {
        var prev = Environment.GetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES", "12");
            Assert.Equal(TimeSpan.FromMinutes(12), AguiSessionRunner.ResolveTurnHttpTimeout());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES", prev);
        }
    }
}
