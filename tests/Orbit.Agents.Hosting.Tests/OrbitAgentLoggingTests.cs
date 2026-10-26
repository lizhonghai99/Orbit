using Orbit.Agents.Hosting;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class OrbitAgentLoggingTests
{
    [Theory]
    [InlineData("mail", "mail")]
    [InlineData("Coding Assistant", "coding-assistant")]
    [InlineData("a/b", "a-b")]
    [InlineData("  ", "agent")]
    public void SanitizeFileToken_keeps_path_safe(string input, string expected) =>
        Assert.Equal(expected, OrbitAgentLogging.SanitizeFileToken(input));
}
