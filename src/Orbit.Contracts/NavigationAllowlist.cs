namespace Orbit.Contracts;

/// <summary>
/// Generic host allowlist for Playwright navigation (scheme + host suffix match).
/// Site-specific host lists live with each agent (e.g. <c>MailBrowserPolicy.AllowedHosts</c>).
/// </summary>
public static class NavigationAllowlist
{
    /// <summary>
    /// True when <paramref name="href"/> is http(s) and its host equals or is a subdomain of
    /// one of <paramref name="allowedHostSuffixes"/>. Empty allowlist is fail-closed.
    /// </summary>
    public static bool IsAllowed(string? href, IReadOnlyList<string> allowedHostSuffixes)
    {
        if (allowedHostSuffixes.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(href)
            || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.Host;
        foreach (var suffix in allowedHostSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
