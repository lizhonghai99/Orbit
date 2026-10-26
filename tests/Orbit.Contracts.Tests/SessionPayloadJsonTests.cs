using System.Text.Json;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class SessionPayloadJsonTests
{
    [Fact]
    public void ReadString_returns_null_for_non_string_kinds()
    {
        var payload = JsonSerializer.SerializeToElement(new { kind = 123, status = true, ok = "yes" });
        Assert.Null(SessionPayloadJson.ReadString(payload, "kind"));
        Assert.Null(SessionPayloadJson.ReadString(payload, "status"));
        Assert.Equal("yes", SessionPayloadJson.ReadString(payload, "ok"));
        Assert.Null(SessionPayloadJson.ReadString(payload, "missing"));
    }

    [Fact]
    public void ReadString_from_entry_payload()
    {
        var entry = new SessionEntry
        {
            Id = "1",
            Type = "meta",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { kind = "research_stage" })
        };
        Assert.Equal("research_stage", SessionPayloadJson.ReadString(entry, "kind"));
    }
}
