using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class NavigationAllowlistTests
{
    private static readonly string[] DemoHosts = ["outlook.live.com", "login.live.com"];

    [Theory]
    [InlineData("file:///C:/Users/lizho/.ssh/id_rsa")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("about:blank")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://evil.example/phish")]
    [InlineData("https://evil-outlook.live.com/")]
    [InlineData("https://outlook.live.com.evil.com/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://[::1]/")]
    public void Rejects_schemes_spoofs_and_literals(string href) =>
        Assert.False(NavigationAllowlist.IsAllowed(href, DemoHosts));

    [Theory]
    [InlineData("https://outlook.live.com/mail/")]
    [InlineData("https://OUTLOOK.LIVE.COM/mail/")]
    [InlineData("https://outlook.live.com:443/mail/")]
    [InlineData("https://sub.outlook.live.com/")]
    [InlineData("https://login.live.com/oauth20_authorize.srf")]
    public void Allows_exact_and_subdomain_hosts(string href) =>
        Assert.True(NavigationAllowlist.IsAllowed(href, DemoHosts));

    [Fact]
    public void Empty_allowlist_is_fail_closed() =>
        Assert.False(NavigationAllowlist.IsAllowed("https://outlook.live.com/", []));

    [Fact]
    public void Trailing_dot_host_does_not_match_suffix() =>
        // Uri.Host typically strips trailing dots; if present, suffix match must not open the door.
        Assert.False(NavigationAllowlist.IsAllowed("https://not-outlook.live.com.evil/", DemoHosts));
}
