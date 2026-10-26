using System.Text.Json;
using Microsoft.Playwright;
using Orbit.Contracts;

namespace Orbit.Agents.Browser;

/// <summary>
/// Chromium profiles for agents that need a browser. Starts headless by default;
/// switches to a visible window only when login is required.
/// Named profiles map to <c>{profilesRoot}/{id}/</c> (legacy flat profile = default).
/// Domain / site logic lives in each agent project — not here.
/// </summary>
public sealed class BrowserSessionService : IAsyncDisposable
{
    private readonly string _agentId;
    private readonly string _defaultLoginUrl;
    private readonly string _profilesRoot;
    private readonly bool _preferHeadless;
    private readonly BrowserSessionOptions _options;
    private readonly Func<string?, string?, bool> _looksLikeLoginWall;
    private readonly IReadOnlyList<string> _navigationHosts;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string _activeProfileId;
    private string _profileDir;
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _forceHeaded;

    public string AgentId => _agentId;
    public string DefaultLoginUrl => _defaultLoginUrl;
    public string ProfilesRoot => _profilesRoot;
    public string ActiveProfileId => _activeProfileId;
    public string ProfileDir => _profileDir;
    public bool PreferHeadless => _preferHeadless;
    public bool IsEffectivelyHeadless => !_forceHeaded && _preferHeadless;
    /// <summary>Current launch mode (false after switching to a visible window for login).</summary>
    public bool Headless => IsEffectivelyHeadless;

    public BrowserSessionService(
        string agentId,
        string defaultLoginUrl,
        string profilesRoot,
        bool preferHeadless = true,
        BrowserSessionOptions? options = null)
    {
        _agentId = agentId;
        _defaultLoginUrl = defaultLoginUrl;
        _profilesRoot = profilesRoot;
        _preferHeadless = preferHeadless;
        _options = options ?? BrowserSessionOptions.Default;
        _looksLikeLoginWall = _options.LooksLikeLoginWall ?? DefaultLooksLikeLoginWall;
        _navigationHosts = _options.AllowedNavigationHosts ?? [];
        Directory.CreateDirectory(_profilesRoot);
        (_activeProfileId, _profileDir) = ResolveInitialProfile(_profilesRoot);
        Directory.CreateDirectory(_profileDir);
    }

    /// <summary>
    /// Generic login-wall heuristic (URL contains login / signin / oauth).
    /// Agents with site-specific SSO should pass <see cref="BrowserSessionOptions.LooksLikeLoginWall"/>.
    /// </summary>
    public static bool DefaultLooksLikeLoginWall(string? url, string? title)
    {
        var hay = $"{url} {title}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hay))
        {
            return false;
        }

        return hay.Contains("/oauth", StringComparison.Ordinal)
               || hay.Contains("signin", StringComparison.Ordinal)
               || hay.Contains("sign in", StringComparison.Ordinal)
               || hay.Contains("login", StringComparison.Ordinal);
    }

    private bool LooksLikeLoginWall(string? url, string? title) => _looksLikeLoginWall(url, title);

    /// <summary>Fresh Playwright pages land here after context restart — not a real navigation target.</summary>
    public static bool IsBlankBrowserUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var u = url.Trim();
        return u.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
               || u.Equals("about:srcdoc", StringComparison.OrdinalIgnoreCase)
               || u.StartsWith("chrome://new", StringComparison.OrdinalIgnoreCase)
               || u.StartsWith("edge://new", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// After a headed restart the new page is about:blank — prefer the pre-restart URL when it is real.
    /// </summary>
    public static string ResolveLoginWallNavigationTarget(string? priorUrl, string defaultLoginUrl) =>
        !IsBlankBrowserUrl(priorUrl) ? priorUrl!.Trim() : defaultLoginUrl;

    /// <summary>Resolve on-disk folder for a profile id (creates directory).</summary>
    public string EnsureProfileDirectory(string? profileId)
    {
        var id = BrowserProfileIds.Sanitize(profileId);
        if (string.IsNullOrWhiteSpace(id))
        {
            id = "default";
        }

        // Legacy: Chromium files lived directly under profilesRoot.
        if (string.Equals(id, "default", StringComparison.OrdinalIgnoreCase)
            && LooksLikeChromiumProfile(_profilesRoot)
            && !Directory.Exists(Path.Combine(_profilesRoot, "default")))
        {
            return _profilesRoot;
        }

        var path = Path.Combine(_profilesRoot, id);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Switch active Chromium profile (closes current browser). Returns new profile path.
    /// Fails fast if another browser op holds the gate (avoids killing an in-flight tool).
    /// </summary>
    public async Task<string> SwitchProfileAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Browser is busy with another tool call. Finish or stop the current run before switching profile.");
        }

        try
        {
            var id = BrowserProfileIds.Sanitize(profileId);
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Profile id is required.", nameof(profileId));
            }

            var path = EnsureProfileDirectory(id);
            if (string.Equals(_activeProfileId, id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_profileDir, path, StringComparison.OrdinalIgnoreCase)
                && _page is not null)
            {
                return _profileDir;
            }

            _activeProfileId = id;
            _profileDir = path;
            _forceHeaded = false;
            await RestartCoreAsync(cancellationToken).ConfigureAwait(false);
            return _profileDir;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (string Id, string Path) ResolveInitialProfile(string profilesRoot)
    {
        var namedDefault = Path.Combine(profilesRoot, "default");
        if (Directory.Exists(namedDefault))
        {
            return ("default", namedDefault);
        }

        if (LooksLikeChromiumProfile(profilesRoot))
        {
            return ("default", profilesRoot);
        }

        Directory.CreateDirectory(namedDefault);
        return ("default", namedDefault);
    }

    private static bool LooksLikeChromiumProfile(string dir) =>
        Directory.Exists(Path.Combine(dir, "Default"))
        || File.Exists(Path.Combine(dir, "Local State"))
        || File.Exists(Path.Combine(dir, "Preferences"));

    public async Task<T> WithPageAsync<T>(
        Func<IPage, CancellationToken, Task<T>> action,
        bool ensureStarted = true,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ensureStarted)
            {
                await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_page is null)
            {
                throw new InvalidOperationException("Browser page is not started.");
            }

            return await action(_page, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restart Chromium with a visible window so the user can log in.</summary>
    public async Task EnsureVisibleForLoginAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_preferHeadless || _forceHeaded)
            {
                if (_page is null)
                {
                    await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            _forceHeaded = true;
            await RestartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// If the current page looks like a login wall, flip to headed and reopen the login URL.
    /// Call after navigations that may bounce to SSO.
    /// </summary>
    public async Task<bool> RevealIfLoginWallAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_page is null)
            {
                return false;
            }

            var priorUrl = _page.Url;
            if (!LooksLikeLoginWall(priorUrl, await SafeTitleAsync(_page).ConfigureAwait(false)))
            {
                return false;
            }

            // Snapshot before RestartCoreAsync — the new page starts at about:blank.
            var target = ResolveLoginWallNavigationTarget(priorUrl, _defaultLoginUrl);

            if (_preferHeadless && !_forceHeaded)
            {
                _forceHeaded = true;
                await RestartCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_page is null)
            {
                return false;
            }

            try
            {
                await GotoCancellableAsync(_page, target, 60_000, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("url_not_allowed", StringComparison.Ordinal))
            {
                // Snapshot URL left the allowlist — fall back to configured login URL once.
                if (!string.Equals(target, _defaultLoginUrl, StringComparison.OrdinalIgnoreCase))
                {
                    await GotoCancellableAsync(_page, _defaultLoginUrl, 60_000, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> OpenLoginPageAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        await EnsureVisibleForLoginAsync(cancellationToken).ConfigureAwait(false);

        return await WithPageAsync(async (page, ct) =>
        {
            var target = string.IsNullOrWhiteSpace(url) ? _defaultLoginUrl : url!;
            if (!IsNavigationAllowed(target))
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "url_not_allowed",
                    url = target,
                    agentId = _agentId,
                    action = "open_login_page"
                });
            }

            await GotoCancellableAsync(page, target, 60_000, ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                agentId = _agentId,
                action = "open_login_page",
                url = page.Url,
                title = await page.TitleAsync().ConfigureAwait(false),
                profileId = _activeProfileId,
                profileDir = _profileDir,
                headless = IsEffectivelyHeadless,
                hint = "Opened a visible browser window. Complete login (QR or credentials)."
            });
        }, ensureStarted: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Allowlist gate shared by every Playwright navigation from this service.</summary>
    public bool IsNavigationAllowed(string? url) =>
        _navigationHosts.Count > 0 && NavigationAllowlist.IsAllowed(url, _navigationHosts);

    /// <summary>Playwright Goto has no CT; race it so Shell Stop can abort long navigations.</summary>
    private async Task GotoCancellableAsync(
        IPage page,
        string url,
        float timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!IsNavigationAllowed(url))
        {
            throw new InvalidOperationException($"url_not_allowed:{url}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var gotoTask = page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = timeoutMs
        });
        var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
        var completed = await Task.WhenAny(gotoTask, cancelTask).ConfigureAwait(false);
        if (completed == cancelTask)
        {
            try
            {
                // Best-effort abort in-flight navigation; ignore dispose races.
                await page.EvaluateAsync("() => window.stop()").ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            // Observe gotoTask so cancellation cannot leave an UnobservedTaskException.
            try
            {
                await gotoTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored — navigation aborted or failed after stop()
            }

            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await gotoTask.ConfigureAwait(false);
    }

    public async Task<string> GetBrowserStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_page is null)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    started = false,
                    profileId = _activeProfileId,
                    profileDir = _profileDir,
                    preferHeadless = _preferHeadless,
                    headless = IsEffectivelyHeadless,
                    hint = "Browser not started yet (headless by default; a window opens when login is needed)."
                });
            }

            var title = await SafeTitleAsync(_page).ConfigureAwait(false);
            var loginWall = LooksLikeLoginWall(_page.Url, title);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                started = true,
                url = _page.Url,
                title,
                profileId = _activeProfileId,
                profileDir = _profileDir,
                preferHeadless = _preferHeadless,
                headless = IsEffectivelyHeadless,
                loginWall,
                hint = loginWall
                    ? "Looks like a login page; call open_login_page to finish sign-in in a visible window."
                    : "Browser session ready."
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> WaitForUrlChangeAsync(int timeoutSeconds = 120, CancellationToken cancellationToken = default)
    {
        // Snapshot start URL under the gate, then release so other tools are not blocked
        // for up to 300s while polling.
        string startUrl;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_page is null)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "Browser not started." });
            }

            startUrl = _page.Url;
        }
        finally
        {
            _gate.Release();
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 300));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_page is null)
                {
                    return JsonSerializer.Serialize(new { ok = false, error = "Browser not started." });
                }

                if (!string.Equals(_page.Url, startUrl, StringComparison.Ordinal))
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = true,
                        changed = true,
                        from = startUrl,
                        url = _page.Url,
                        title = await _page.TitleAsync().ConfigureAwait(false),
                        loginWall = LooksLikeLoginWall(_page.Url, await SafeTitleAsync(_page).ConfigureAwait(false))
                    });
                }

                if (DateTime.UtcNow >= deadline)
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = true,
                        changed = false,
                        url = _page.Url,
                        title = await _page.TitleAsync().ConfigureAwait(false)
                    });
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_page is null)
            {
                return JsonSerializer.Serialize(new { ok = false, error = "Browser not started." });
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                changed = false,
                url = _page.Url,
                title = await _page.TitleAsync().ConfigureAwait(false)
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureStartedCoreAsync(CancellationToken cancellationToken)
    {
        if (_page is not null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IPlaywright? playwright = null;
        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            _context = await playwright.Chromium.LaunchPersistentContextAsync(_profileDir, new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = IsEffectivelyHeadless,
                Locale = _options.Locale,
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept-Language"] = _options.AcceptLanguage
                },
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                Args = ["--disable-blink-features=AutomationControlled"]
            }).ConfigureAwait(false);

            _playwright = playwright;
            playwright = null; // ownership transferred
            _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync().ConfigureAwait(false);
        }
        catch
        {
            if (_context is not null)
            {
                try { await _context.CloseAsync().ConfigureAwait(false); } catch { /* ignore */ }
                _context = null;
            }

            // After ownership transfer local playwright is null — dispose the field instead.
            if (playwright is not null)
            {
                playwright.Dispose();
            }
            else
            {
                _playwright?.Dispose();
            }

            _playwright = null;
            _page = null;
            throw;
        }
    }

    private async Task RestartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_context is not null)
            {
                await _context.CloseAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Browser may already be crashed; still clear fields so callers do not reuse a dead page.
        }
        finally
        {
            try
            {
                _playwright?.Dispose();
            }
            catch
            {
                // ignored
            }

            _page = null;
            _context = null;
            _playwright = null;
        }

        await EnsureStartedCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> SafeTitleAsync(IPage page)
    {
        try
        {
            return await page.TitleAsync().ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        // WaitForUrlChange can hold the gate up to ~300s; do not block host shutdown forever.
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            try
            {
                _gate.Dispose();
            }
            catch
            {
                // ignored
            }

            return;
        }

        try
        {
            if (_context is not null)
            {
                await _context.CloseAsync().ConfigureAwait(false);
            }

            _playwright?.Dispose();
            _page = null;
            _context = null;
            _playwright = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
