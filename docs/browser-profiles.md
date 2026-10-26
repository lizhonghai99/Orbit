# Browser profiles (disk use)

Chromium user-data for Mail (and any future browser agent) lives under:

```text
{ORBIT_ROOT}/data/browser-profiles/{agentId}/{profileId}/
```

Each profile keeps cookies, local storage, and cache — **this grows without bound** with normal browsing.

## Policy (0.0.1)

- Profiles are **gitignored** (`data/` and `**/browser-profiles/`).
- Orbit does **not** auto-delete profiles (login state is valuable).
- Operators reclaim disk by deleting unused profile folders while the agent is stopped.

## Cleanup

```powershell
# Stop Mail agent first (Shell → unload, or stop Runtime).
Remove-Item -Recurse -Force .\data\browser-profiles\mail\<profileId>
```

Or wipe all Mail profiles and re-login:

```powershell
Remove-Item -Recurse -Force .\data\browser-profiles\mail
```

## Navigation allowlist (not a sandbox)

Host allowlists gate **explicit** `page.Goto` / Mail href-open only. Popup (`window.open`) targets and in-flight HTTP redirects are **not** re-checked — the headed Chromium window is operated with a human present. See [agents.md · Mail](agents.md).

## Related

- [agents.md](agents.md) · Mail memory / browser profile row  
- `Orbit.Agents.Browser` owns profile directory layout only  
