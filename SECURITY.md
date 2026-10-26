# Security Policy

## Supported versions

Orbit **0.0.1** is a local-desktop research/learning build. There is no long-term support channel yet; fixes land on `main`.

## Threat model (local)

- Runtime binds **127.0.0.1** and requires `X-Orbit-Token` (except `/health`).
- Agent processes use a separate in-memory token injected by Runtime.
- **Coding Assistant `bash`** allowlists interpreters such as `node` / `python` / `npm` / `pip`. That is **user-level arbitrary code execution** inside the workspace. v0.0.1 product stance: **only open workspaces you trust**; this is not an AppContainer/sandbox. See [docs/agents-zh.md](docs/agents-zh.md).
- Browser profiles and session logs live on the local disk under the repo `data/` tree and `%LOCALAPPDATA%/Orbit/`.
- **Logs may contain session content** (user/assistant text, tool names and arguments) at Information level under `%LOCALAPPDATA%/Orbit/logs/`. Treat log files as sensitive data; do not share or sync them to untrusted locations.

## Reporting a vulnerability

Please open a **private** GitHub security advisory (or email the maintainer if advisories are unavailable). Do not file public issues for exploitable local-escape or token-leak bugs.

Include:

1. Orbit version / commit  
2. OS and reproduction steps  
3. Impact (session leak, workspace escape, token theft, etc.)

We aim to acknowledge within 7 days.
