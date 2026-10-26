using Microsoft.Extensions.DependencyInjection;
using Orbit.Contracts;

namespace Orbit.Agents.Browser;

public static class BrowserHostExtensions
{
    public static BrowserSessionService AddOrbitBrowser(
        this IServiceCollection services,
        string agentId,
        string defaultLoginUrl,
        BrowserSessionOptions? options = null)
    {
        var repoRoot = OrbitPathNames.TryGetOrbitRoot();
        var profileRoot = !string.IsNullOrWhiteSpace(repoRoot)
            ? Path.Combine(repoRoot, "data", "browser-profiles", OrbitPathNames.SanitizeFileName(agentId))
            : Path.Combine(AppContext.BaseDirectory, "browser-profiles", OrbitPathNames.SanitizeFileName(agentId));

        // Default headless. Set ORBIT_BROWSER_HEADLESS=0 to always show the window.
        var headlessEnv = Environment.GetEnvironmentVariable("ORBIT_BROWSER_HEADLESS");
        var preferHeadless = !string.Equals(headlessEnv, "0", StringComparison.OrdinalIgnoreCase);

        var loginUrl = Environment.GetEnvironmentVariable("ORBIT_LOGIN_URL");
        if (string.IsNullOrWhiteSpace(loginUrl))
        {
            loginUrl = defaultLoginUrl;
        }

        var browser = new BrowserSessionService(agentId, loginUrl, profileRoot, preferHeadless, options);
        services.AddSingleton(browser);
        return browser;
    }
}
