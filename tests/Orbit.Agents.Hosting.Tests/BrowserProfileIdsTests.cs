using Orbit.Agents.Browser;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class BrowserProfileIdsTests
{
    [Fact]
    public void Sanitize_null_returns_empty()
    {
        Assert.Equal("", BrowserProfileIds.Sanitize(null));
    }

    [Fact]
    public void Sanitize_whitespace_returns_empty()
    {
        Assert.Equal("", BrowserProfileIds.Sanitize(" "));
    }

    [Fact]
    public void Ascii_names_slugify()
    {
        Assert.Equal("work-mail", BrowserProfileIds.Sanitize("Work Mail"));
    }

    [Fact]
    public void NonAscii_names_get_stable_distinct_ids()
    {
        var a = BrowserProfileIds.Sanitize("工作邮箱");
        var b = BrowserProfileIds.Sanitize("私人邮箱");
        Assert.StartsWith("mb-", a);
        Assert.StartsWith("mb-", b);
        Assert.NotEqual(a, b);
        Assert.Equal(a, BrowserProfileIds.Sanitize("工作邮箱"));
    }
}
