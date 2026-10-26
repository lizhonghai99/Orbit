using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class SessionEventsMetaTests
{
    [Fact]
    public void Meta_mcp_changed_includes_action_and_source()
    {
        var req = SessionEvents.Meta(SessionMetaKinds.McpChanged, new
        {
            action = "trust",
            serverId = "tavily",
            source = "shell"
        });

        SessionEvents.Validate(req);
        Assert.Equal(SessionEventTypes.Meta, req.Type);
        Assert.NotNull(req.Payload);
        var payload = req.Payload!.Value;
        Assert.Equal(SessionMetaKinds.McpChanged, payload.GetProperty("kind").GetString());
        Assert.Equal("trust", payload.GetProperty("action").GetString());
        Assert.Equal("tavily", payload.GetProperty("serverId").GetString());
        Assert.Equal("shell", payload.GetProperty("source").GetString());
        Assert.Equal(SessionEventTypes.SchemaVersion, payload.GetProperty("v").GetInt32());
    }

    [Fact]
    public void Meta_requires_kind()
    {
        Assert.Throws<ArgumentException>(() => SessionEvents.Meta(" "));
    }

    [Theory]
    [InlineData(SessionEventTypes.User, true)]
    [InlineData("USER", true)]
    [InlineData(SessionEventTypes.Assistant, false)]
    [InlineData(SessionEventTypes.Approval, false)]
    [InlineData(SessionEventTypes.Meta, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPubliclyAppendable_only_user(string? type, bool expected) =>
        Assert.Equal(expected, SessionEventTypes.IsPubliclyAppendable(type));
}
