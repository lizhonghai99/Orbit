using System.ComponentModel;

namespace Orbit.Agents.Browser;

/// <summary>Generic browser tools. Domain tools live in each agent project.</summary>
public sealed class BrowserTools(BrowserSessionService browser)
{
    [Description("Open the site login page in a visible Chromium window so the user can scan QR or enter password. Use only when login is needed.")]
    public Task<string> OpenLoginPageAsync(
        [Description("Optional override URL; defaults to the agent login URL")] string? url = null,
        CancellationToken cancellationToken = default)
        => browser.OpenLoginPageAsync(url, cancellationToken);

    [Description("Return current browser URL, title, headless mode, and whether a login wall is showing.")]
    public Task<string> GetBrowserStatusAsync(CancellationToken cancellationToken = default)
        => browser.GetBrowserStatusAsync(cancellationToken);

    [Description("Wait until the browser URL changes (useful after the user finishes QR/password login).")]
    public Task<string> WaitForUrlChangeAsync(
        [Description("Timeout in seconds")] int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
        => browser.WaitForUrlChangeAsync(timeoutSeconds, cancellationToken);
}
