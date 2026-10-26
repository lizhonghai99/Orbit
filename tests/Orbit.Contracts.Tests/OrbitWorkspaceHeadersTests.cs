using System.Net.Http.Headers;
using System.Text;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class OrbitWorkspaceHeadersTests
{
    [Fact]
    public void EncodeDecode_roundtrips_unicode_path()
    {
        var path = @"D:\项目\demo";
        var encoded = OrbitWorkspaceHeaders.EncodePath(path);
        Assert.StartsWith("b64:", encoded);
        Assert.Equal(path, OrbitWorkspaceHeaders.DecodePath(encoded));
    }

    [Fact]
    public void ApplyTo_sets_b64_path_and_id()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost/ag-ui");
        OrbitWorkspaceHeaders.ApplyTo(req.Headers, @"D:\LearnCode\Demo", "abc");

        Assert.True(req.Headers.TryGetValues(OrbitWorkspaceHeaders.Workspace, out var paths));
        var encoded = Assert.Single(paths);
        Assert.Equal(@"D:\LearnCode\Demo", OrbitWorkspaceHeaders.DecodePath(encoded));
        Assert.True(req.Headers.TryGetValues(OrbitWorkspaceHeaders.WorkspaceId, out var ids));
        Assert.Equal("abc", Assert.Single(ids));
    }

    [Fact]
    public void DecodePath_accepts_legacy_plaintext()
    {
        Assert.Equal(@"D:\ws", OrbitWorkspaceHeaders.DecodePath(@"D:\ws"));
    }

    [Fact]
    public void ApplyTo_session_header_skips_empty()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost/ag-ui");
        var session = new SessionHeader
        {
            SessionId = "s",
            AgentId = "coder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        OrbitWorkspaceHeaders.ApplyTo(req.Headers, session);
        Assert.False(req.Headers.Contains(OrbitWorkspaceHeaders.Workspace));
    }

    [Fact]
    public void ApplyTo_from_session_with_snapshot()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost/ag-ui");
        var session = new SessionHeader
        {
            SessionId = "s",
            AgentId = "coder",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            WorkspaceId = "w1",
            WorkspacePath = @"D:\ws"
        };
        OrbitWorkspaceHeaders.ApplyTo(req.Headers, session);
        Assert.True(req.Headers.TryGetValues(OrbitWorkspaceHeaders.Workspace, out var paths));
        Assert.Equal(@"D:\ws", OrbitWorkspaceHeaders.DecodePath(Assert.Single(paths)));
    }

    [Fact]
    public void EncodePath_is_ascii_safe_for_headers()
    {
        var encoded = OrbitWorkspaceHeaders.EncodePath(@"D:\中文\路径");
        Assert.True(encoded.All(c => c < 128));
        _ = Encoding.ASCII.GetBytes(encoded); // must not throw
    }
}
