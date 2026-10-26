using Orbit.Agents.Browser;
using Orbit.Contracts;

namespace Orbit.Agents.Mail.Mail;

/// <summary>Mail-specific browser knobs (Outlook / Microsoft login surfaces).</summary>
public static class MailBrowserPolicy
{
    /// <summary>Hosts Mail tools / Playwright may navigate to (exact or subdomain suffix).</summary>
    public static readonly string[] AllowedHosts =
    [
        "outlook.live.com",
        "outlook.office.com",
        "outlook.office365.com",
        "login.microsoftonline.com",
        "login.microsoft.com",
        "login.live.com",
        "account.live.com"
    ];

    /// <summary>en-US chrome so Outlook Web DOM selectors stay English-only.</summary>
    public static BrowserSessionOptions SessionOptions { get; } = new()
    {
        Locale = "en-US",
        AcceptLanguage = "en-US,en;q=0.9",
        LooksLikeLoginWall = LooksLikeLoginWall,
        AllowedNavigationHosts = AllowedHosts
    };

    /// <summary>
    /// Microsoft account / OAuth login walls. Safe with <paramref name="title"/> null
    /// (URL-only check used when deciding whether to reopen the default login URL).
    /// </summary>
    public static bool LooksLikeLoginWall(string? url, string? title)
    {
        var hay = $"{url} {title}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hay))
        {
            return false;
        }

        return hay.Contains("login.live.com", StringComparison.Ordinal)
               || hay.Contains("login.microsoftonline.com", StringComparison.Ordinal)
               || hay.Contains("login.microsoft.com", StringComparison.Ordinal)
               || hay.Contains("account.live.com", StringComparison.Ordinal)
               || hay.Contains("/oauth", StringComparison.Ordinal)
               || hay.Contains("signin", StringComparison.Ordinal)
               || hay.Contains("sign in", StringComparison.Ordinal)
               || hay.Contains("登录", StringComparison.Ordinal)
               || hay.Contains("帳戶登入", StringComparison.Ordinal);
    }
}
