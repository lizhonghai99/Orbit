# Orbit

A long-running **Runtime** supervises independent agent processes; the **Avalonia Shell** is a thin client. The session tree lives in Runtime so a future Web/CLI shell can share the same events and replay model.

**Version**: 0.0.1 · License: [MIT](LICENSE) · [中文说明](README-zh.md)

Further reading: [Architecture](docs/architecture.md) · [Agents](docs/agents.md) · [Security](SECURITY.md) · [Changelog](CHANGELOG.md) · [Contributing](CONTRIBUTING.md)

## Requirements

| Item | Notes |
|------|--------|
| OS | **Windows** (Shell / Playwright / Git Bash / DPAPI validated; CI is `windows-latest`) |
| SDK | .NET **10** pinned in [`global.json`](global.json) |
| Coder | **Git Bash** (`bash.exe`; override with `ORBIT_BASH_PATH`) — no PowerShell fallback |
| Mail | Playwright Chromium (install once after build) |

## Visual Studio

1. Open `Orbit.slnx` at the repo root.
2. **Build → Build Solution** (Release recommended; `TreatWarningsAsErrors` is on).
3. Prefer multiple startup projects: right-click the solution → **Configure Startup Projects** → set both `Orbit.Runtime` and `Orbit.Shell` to **Start**.
4. Or F5 `Orbit.Runtime` first, then use “Start Runtime” in Shell (if something already listens on 5050, just refresh).
5. **Run only one Runtime** (a second instance for the same repo will clean up the other’s agent processes). Shell reads `%LOCALAPPDATA%/Orbit/runtime.token` and sends `X-Orbit-Token`.

Default URL: `http://127.0.0.1:5050`.

## Layout

| Project | Role |
|---------|------|
| `Orbit.Contracts` | Manifests, DTOs, auth helpers, error codes |
| `Orbit.Runtime` | Discovery / lifecycle / session JSONL / approvals / SSE |
| `Orbit.Shell` | Thin UI: chats, model switch, settings, capabilities (Skills / Tools / **MCP**) |
| `Orbit.Agents.Hosting` | Agent **platform**: AG-UI host, Responses factory, skills, MCP, harness |
| `Orbit.Agents.Browser` | Shared Playwright pack (profiles only; no domain logic) |
| `Orbit.Agents.Mail` | Outlook Web triage (HITL; never auto-delete) |
| `Orbit.Agents.Research` | BackgroundAgents + Workflow Scope→Outline→Draft + checkpoints |
| `Orbit.Agents.Coder` | Short-Running / Long-Running (`coder_*` handoff + FeatureList); Workspace required |
| `agents/*.json` | Static discovery (top-level only; `deferred/` not loaded) |
| `data/` | Local sessions / profiles / MCP / workspaces (**gitignored**) |

Tests: `Orbit.Contracts.Tests`, `Orbit.Agents.Hosting.Tests`, `Orbit.Agents.Coder.Tests`, `Orbit.Runtime.Tests`, `Orbit.Shell.Tests` (**378** passing as of this README; Coder includes handoff approval-resume contract tests).

### Skills (per agent)

- Path: `src/Orbit.Agents.{Mail|Research|Coder}/skills/<skill-id>/SKILL.md`
- Injected at agent start (`SkillCatalog.BuildInstructionsAppendix`)
- In-process `GET /skills`; Runtime `GET /agents/{id}/skills`; Shell right pane
- Load and execute **inside the agent process** (same as MCP / tools)

### Attachments

- Shell `+` → file → `POST /sessions/{id}/attachments`
- Runtime stores under `data/attachments/{agentId}/{sessionId}/…`, appends `meta.attachment_uploaded`
- If the agent is Running, Runtime forwards to agent `POST /attachments` (not yet injected into model context)

## What works today

- Discover agents from `agents/*.json`; Runtime start/stop + `/health`
- Hand-wired Harness with **per-agent whitelist** (`agents/*.json` → `ORBIT_HARNESS`). Default when omitted: `mode,todo,skills,compaction,tool_approval`. Disk providers (`file_access` / `file_memory`) and `background` are opt-in. If the manifest omits `tool_approval` but ApprovalRequired tools exist, Hosting auto-enables it and logs a warning.
- Platform `ask_user_choice` / `ask_user_confirm` always injected (Shell cards)
- AG-UI: `POST /sessions/{id}/run`, Stop cancel, approval resume
- Sessions: append-only JSONL + `.meta.json`; delete/archive (archive builds a Summary)
- **Incremental sync + SSE**: `GET /sessions/{id}?after=` and `GET /sessions/{id}/events` — Shell uses SSE while a turn is busy (no 1.2s full pull)
- **Host Workspace**: `data/workspaces.json` single active folder; session create snapshots path; runs send `X-Orbit-Workspace` → `OrbitRunContext`; confirm before interrupting runs; path change cancels + restarts agents
- Shell: zh/en i18n, workspace chip, chat|**trace** views, Stop + follow-up, reasoning toggle/effort, History archive/delete
- Telemetry: FunctionInvoker spans + `X-Orbit-Run-Id`; `tool_catalog` once per session
- LLM settings: DeepSeek / Moonshot / Custom → `%LOCALAPPDATA%/Orbit/llm.settings.json` (API keys **DPAPI** CurrentUser). Import from env is explicit (`ImportFromEnvironment`), not silent on startup. Model switch reloads agents.
- **Local auth (dual token)**: Shell ↔ Runtime uses the persisted Shell token (`runtime.token`). Runtime ↔ Agent uses a separate **in-memory Agent token** (injected as `ORBIT_RUNTIME_TOKEN`; never written to disk; Shell never sees it). Header is still `X-Orbit-Token`; `/health` exempt (Agent `/health` reports `authRequired`).
- API errors: `{ "code", "message", "details?" }` (`OrbitErrorCodes`)
- Logs: Serilog rolling files under `%LOCALAPPDATA%/Orbit/logs/` (`runtime-*.log`, `agent-{id}-*.log`, `shell-*.log`, 14 days). May contain session content — treat as sensitive ([SECURITY.md](SECURITY.md)).

### Workspace

| Capability | Notes |
|------------|--------|
| Single active | Open / Activate; reuse same path; reject drive roots and system dirs |
| Session snapshot | Frozen at create; immutable afterward |
| Per-run root | FileAccess / Coder `bash` read `OrbitRunContext.Workspace` |
| Mismatch | Session path ≠ agent start path → `409 workspace_mismatch` |
| Switch + restart | In-flight runs require `confirm`; else cancel + Restart |
| Symlinks | Open resolves junction/symlink **final** targets before unsafe-root check (fail-closed if unresolvable) |

**Coder bash guardrail**: Git Bash only (`ORBIT_BASH_PATH` to override). Fail closed if bash is missing — policy is bash-syntax; a PowerShell fallback would be a parser-differential bypass. Deny-by-default executable allowlist (`dotnet`/`git`/`npm`/…); block nested shells + `curl`/`wget` + `$()`/backticks/`..`/`~`/`$HOME`/`$env:`; absolute paths must stay under workspace; secret-free env whitelist (real `HOME`/`USERPROFILE` kept so git can read `~/.gitconfig`); Windows Job Object cages the process tree (kill-on-close; default ≤32 processes / 2GB via `ORBIT_BASH_JOB_MAX_PROCESSES` / `ORBIT_BASH_JOB_MEMORY_MB`).

This is a **first layer, not a sandbox**. **0.0.1 product stance:** open **trusted workspaces only**. Allowlisted `node`/`python`/`npm`/`pip`/`dotnet` are arbitrary code execution + network. See [SECURITY.md](SECURITY.md) and [agents.md · Coding](docs/agents.md).

**Known limits**: MCP stdio cwd is process-level `ORBIT_WORKSPACE` at client create (not per-run; refresh via agent restart). No FileAccess read-only / subdirectory allowlist. Attachments are stored but not yet fed to the model.

### Harness (built-in manifests)

| Agent | `harness` | Notes |
|-------|-----------|--------|
| Mail | `mode,todo,file_memory,skills,compaction,tool_approval` | No FileAccess; notes share mailbox registry root |
| Coder | `mode,todo,file_memory,file_access,skills,compaction,tool_approval` | `coder_generator` implements; `coder_evaluator` PASS/FAIL QA |
| Research | above + `background` | Full research stack |
| (default if empty) | `mode,todo,skills,compaction,tool_approval` | Disk / Background still opt-in |

Historical single-sample inputTokens (2026-10-26, lean shapes **before** Mail/Coder enabled `file_memory`) lived in earlier drafts — **re-measure after tagging** if you need current numbers. Domain tools still add on top of Harness schemas.

## LLM (OpenAI Responses)

1. Shell → **LLM settings**
2. Pick DeepSeek or Moonshot; confirm Responses endpoint (a mistaken `.../anthropic` path is normalized)
3. Enter API key, **fetch models**, save → `%LOCALAPPDATA%/Orbit/llm.settings.json`
4. Load an agent and chat; the model decides tools

Process env (injected by Runtime for the **active** profile only):

| Variable | Meaning |
|----------|---------|
| `ORBIT_LLM_PROFILE` | `deepseek` / `moonshot` / `custom` |
| `ORBIT_LLM_MODEL` | Current model id |
| `ORBIT_LLM_PROTOCOL` | `responses` |
| `DEEPSEEK_API_KEY` / `DEEPSEEK_END_POINT` | DeepSeek |
| `MOONSHOT_API_KEY` / `MOONSHOT_END_POINT` | Moonshot (`https://api.moonshot.cn/v1` for Responses) |
| `ORBIT_REASONING_ENABLED` | `1`/`0` |
| `ORBIT_REASONING_EFFORT` | `low` / `medium` / `high` / `extrahigh` |

## Mail agent (Outlook Web)

1. Load **Mail**, new chat
2. “打开登录页” → sign in in Chromium
3. “进入收件箱” → “整理收件箱”
4. Approve/reject `approval` / `ask_user_*` cards
5. Approving archive opens the mail and tries Outlook **Archive** (**never auto-deletes**)

Browser profiles: `data/browser-profiles/{agentId}/{profileId}/` — see [docs/browser-profiles.md](docs/browser-profiles.md).

```powershell
pwsh src/Orbit.Agents.Mail/bin/Debug/net10.0/playwright.ps1 install chromium
```

## Coder (Short-Running / Long-Running)

Externally one Coder. Internally two routes + four handoff ids:

| Route | Chain |
|-------|--------|
| **Short-Running** | `coder_router` → `coder_generator` → `coder_evaluator` (HITL) |
| **Long-Running** | `coder_router` → `coder_planner`; each batch `run_coding_turn` (**context reset**) |

| Id | Role |
|----|------|
| `coder_router` | Pick Short-Running vs Long-Running |
| `coder_planner` | `run_feature_list` (resume/reset) + **`run_coding_turn`** (approval gate → fresh G/E) |
| `coder_generator` | Short-Running implement; Long-Running nested inside coding turn |
| `coder_evaluator` | Short-Running QA; Long-Running nested PASS/FAIL (**read-only** FileAccess) |

Long-Running notes: `run_coding_turn` pops a Shell approval card, then claims ready features and runs Generator/Evaluator on **new** `AgentSession`s (15‑min budget; FeatureList seed only). Existing list → `resume=true` / `reset=true` (reset archives). Short-Running FailGate is **per run** (advisory); hard-ish outer stop is ~80 messages on the handoff run.

Details: [docs/agents.md · Coding](docs/agents.md).

## MCP (typically Research)

MCP connects at **agent process start** (no hot-plug). Config: `data/mcp/{agentId}.json` (gitignored). Examples: `docs/examples/`.

| Field | Notes |
|-------|--------|
| `transport` | `stdio` or `http` (https only) |
| `trusted` | Default **false**; untrusted servers are not connected |
| `enabled` | false → skipped |
| `toolPrefix` | `null` → `mcp_{id}_`; `""` keeps server names |
| `timeoutSeconds` | Shared Create / ListTools / Call (default 30) |
| `requiresApproval` / `approvalTools` | HITL for MCP mutations |
| `env` / `headers` | `${VAR}` expansion; never commit plaintext keys |

Shell capabilities pane can add/remove servers and toggle trust; **Restart agent** after save. Audit: `data/audit/mcp.jsonl`.

```json
{
  "servers": [{
    "id": "tavily",
    "transport": "http",
    "url": "https://mcp.tavily.com/mcp/",
    "headers": { "Authorization": "Bearer ${TAVILY_API_KEY}" },
    "trusted": true,
    "enabled": true,
    "toolPrefix": "",
    "timeoutSeconds": 30
  }]
}
```

## Roadmap

**In 0.0.1**

- Session `?after=` + SSE `/sessions/{id}/events`
- Dual-token local auth; structured API errors
- Coder: Git Bash-only + trusted-workspace stance
- CI: Release build (warnings as errors), tests ×2, MTP coverage artifact, `dotnet format --verify-no-changes`

**Later**

- Attachments into model context; Anthropic Messages
- MCP hot-plug; MCP stdio cwd follows per-run workspace
- Workspace read-only / subdirectory allowlist; deeper bash isolation
- Coverage % gate (collect-only today)

## Docs language

| Surface | Language |
|---------|----------|
| `README.md` / `docs/architecture.md` / `docs/agents.md` | English |
| `README-zh.md` / `docs/architecture-zh.md` / `docs/agents-zh.md` | Chinese |
| C# XML / inline comments | **English only** |
| Shell UI | zh + en via `I18n/strings.*.json` |

## CLI smoke

```bash
dotnet restore Orbit.slnx -p:Configuration=Release
dotnet build Orbit.slnx -c Release --no-restore
dotnet test Orbit.slnx -c Release --no-build
dotnet run --project src/Orbit.Runtime
# other terminal
$token = Get-Content "$env:LOCALAPPDATA/Orbit/runtime.token"
curl -H "X-Orbit-Token: $token" http://127.0.0.1:5050/agents
```
