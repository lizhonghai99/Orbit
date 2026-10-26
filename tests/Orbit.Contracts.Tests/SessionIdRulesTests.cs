using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public sealed class SessionIdRulesTests
{
    [Fact]
    public void Accepts_guid_N_format()
    {
        var id = Guid.NewGuid().ToString("N");
        Assert.True(SessionIdRules.IsValid(id));
        Assert.Equal(id, SessionIdRules.RequireValid(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../secrets")]
    [InlineData("..\\..\\Windows")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggg")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Rejects_path_traversal_and_non_hex(string? id)
    {
        Assert.False(SessionIdRules.IsValid(id));
        Assert.Throws<ArgumentException>(() => SessionIdRules.RequireValid(id));
    }
}
