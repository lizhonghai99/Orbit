using Orbit.Agents.Browser;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class BrowserLoginWallTests
{
    [Fact]
    public void Default_matches_generic_login_urls_with_null_title()
    {
        Assert.True(BrowserSessionService.DefaultLooksLikeLoginWall("https://example.com/login", null));
        Assert.True(BrowserSessionService.DefaultLooksLikeLoginWall("https://example.com/oauth/authorize", null));
        Assert.True(BrowserSessionService.DefaultLooksLikeLoginWall("https://example.com/signin", null));
        Assert.False(BrowserSessionService.DefaultLooksLikeLoginWall("https://example.com/inbox", null));
        Assert.False(BrowserSessionService.DefaultLooksLikeLoginWall(null, null));
    }

    [Fact]
    public void Default_does_not_hardcode_microsoft_only_hosts_without_login_token()
    {
        // account.live.com without "login" in URL — Mail injects its own predicate for this.
        Assert.False(BrowserSessionService.DefaultLooksLikeLoginWall("https://account.live.com/", null));
        // login.live.com still matches via generic "login" substring.
        Assert.True(BrowserSessionService.DefaultLooksLikeLoginWall("https://login.live.com/", null));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("about:blank", true)]
    [InlineData("ABOUT:BLANK", true)]
    [InlineData("about:srcdoc", true)]
    [InlineData("chrome://newtab", true)]
    [InlineData("https://login.live.com/", false)]
    public void IsBlankBrowserUrl_detects_fresh_page_placeholders(string? url, bool blank) =>
        Assert.Equal(blank, BrowserSessionService.IsBlankBrowserUrl(url));

    [Theory]
    [InlineData("https://login.live.com/oauth", "https://outlook.live.com/mail/", "https://login.live.com/oauth")]
    [InlineData("about:blank", "https://outlook.live.com/mail/", "https://outlook.live.com/mail/")]
    [InlineData(null, "https://outlook.live.com/mail/", "https://outlook.live.com/mail/")]
    [InlineData("chrome://newtab", "https://default/", "https://default/")]
    public void ResolveLoginWallNavigationTarget_prefers_real_prior_url(
        string? prior,
        string fallback,
        string expected) =>
        Assert.Equal(expected, BrowserSessionService.ResolveLoginWallNavigationTarget(prior, fallback));
}
