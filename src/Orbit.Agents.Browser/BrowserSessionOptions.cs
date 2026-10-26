namespace Orbit.Agents.Browser;

/// <summary>
/// Per-agent browser launch / detection knobs. Defaults keep Mail Outlook DOM selectors working (en-US).
/// </summary>
public sealed class BrowserSessionOptions
{
    public static BrowserSessionOptions Default { get; } = new();

    /// <summary>Chromium locale (default <c>en-US</c>).</summary>
    public string Locale { get; init; } = "en-US";

    /// <summary>Accept-Language header (default <c>en-US,en;q=0.9</c>).</summary>
    public string AcceptLanguage { get; init; } = "en-US,en;q=0.9";

    /// <summary>
    /// Whether the current page looks like a login wall.
    /// When null, <see cref="BrowserSessionService.DefaultLooksLikeLoginWall"/> is used
    /// (generic login / signin / oauth URL heuristics only).
    /// </summary>
    public Func<string?, string?, bool>? LooksLikeLoginWall { get; init; }

    /// <summary>
    /// Host suffixes allowed for Playwright navigation (<see cref="Orbit.Contracts.NavigationAllowlist"/>).
    /// Empty = deny all navigations (fail closed). Mail sets <c>MailBrowserPolicy.AllowedHosts</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedNavigationHosts { get; init; } = [];
}
