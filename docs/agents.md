# Orbit Agents — capability guide

[中文](agents-zh.md) · Version **0.0.1**

This document describes each built-in assistant the way product Agent docs usually do: purpose, harness modules, memory, skills, tools, safety, and limits.  
Agent capabilities prefer framework Harness building blocks; Orbit owns the multi-process host and session tree—not a parallel agent runtime.

See also: [Architecture](architecture.md) · [Harness / HITL spike](spike-p0-hitl-history.md)

---

## Capability dimensions

| Dimension | Meaning |
|-----------|---------|
| **Purpose** | User problem the agent solves |
| **Protocol** | Runtime ↔ agent channel (AG-UI for all current agents) |
| **Workspace** | Host folder policy |
| **Mode / Todo** | Plan/execute mode and checklists |
| **Skills** | Progressive-disclosure `SKILL.md` playbooks |
| **Memory** | FileMemory and related stores |
| **Files** | FileAccess (durable / workspace disk) |
| **Compaction** | Context-window compaction (on for every agent) |
| **Tool approval** | HITL for mutating tools (on for every agent) |
| **Sub-agents** | BackgroundAgents |
| **Orchestration** | Workflow graphs / checkpoints |
| **MCP** | External tools loaded at process start |
| **Domain tools** | Agent-specific tools |
| **Safety** | Hard no’s and required approvals |

**Always injected** (not via `harness`): platform `ask_user_*`.

Manifest `harness` → `ORBIT_HARNESS`; include `compaction` / `tool_approval` when needed. Empty → `mode,todo,skills,compaction,tool_approval` (disk / Background still opt-in).

---

## Matrix

| Dimension | Mail `mail` | Research `research` | Coding `coder` |
|-----------|-------------|---------------------|----------------|
| Purpose | Outlook Web triage | Multi-angle research + briefs | Edit/build/test in workspace |
| Workspace | `none` | `optional` | **`required`** |
| Mode / Todo | ✅ | ✅ | ✅ |
| Skills | ✅ `outlook-triage` | ✅ `concurrent-research` | ✅ `minimal-diff` |
| FileMemory | ✅ | ✅ | ✅ |
| FileAccess | ❌ | ✅ | ✅ (session workspace) |
| Compaction | ✅ | ✅ | ✅ |
| ToolApproval | ✅ | ✅ | ✅ |
| BackgroundAgents | ❌ | ✅ `AngleResearcher` | ❌ |
| Workflow | ❌ | ✅ Scope→Outline→Draft | ❌ |
| MCP | configurable | configurable (often search) | configurable |
| Login | Outlook Web | none | none |
| Typical HITL | folder / move / archive | writes, MCP mutations, ask_user | writes, `bash` |

---

## Mail Assistant

**Manifest**: `agents/mail.agent.json` · Project: `Orbit.Agents.Mail`

### Purpose

Triage Outlook on the web: understand the inbox, propose organize actions, apply folder/move/archive **only after approval**. Never auto-delete.

### Harness

| Module | |
|--------|--|
| Mode / Todo | On |
| Skills | On — `outlook-triage` |
| FileMemory | On — `data/file-memory/mail/` (same root as `MailboxRegistry`) |
| Compaction / ToolApproval | On (listed in manifest) |
| FileAccess / Background | Off |

### Domain tools

Browser login/inbox navigation, multi-mailbox profiles, list/organize (read-only), mutating `create_folder` / `move_mail` / `archive_mail` (each HITL), plus platform `ask_user_*`.

### Memory & context

FileMemory for lasting triage rules / folder plans (mailbox registry lives there too). No FileAccess. Chat via AgentSession + compaction. Browser profiles under `data/browser-profiles/mail/{profileId}/`.

### Safety & limits

No delete; one active approval card; Runtime does not apply mail DTOs. Depends on Outlook Web DOM and a completed Playwright login.

**Navigation gate**: Playwright `Goto` and Mail href-open go through `NavigationAllowlist` + `MailBrowserPolicy.AllowedHosts` (fail-closed when empty). **Not covered** by that gate: `window.open` / popup targets and mid-navigation HTTP redirects chosen by the site — acceptable under the local “human is at the headed window” threat model; do not treat the allowlist as a full browser sandbox.

---

## Research Assistant

**Manifest**: `agents/research.agent.json` · Project: `Orbit.Agents.Research`

### Purpose

1. **BackgroundAgents** — concurrent `AngleResearcher` fact-finding  
2. **Workflow** — `run_research_pipeline` Scope → Outline → Draft with checkpoints  

### Harness

Mode, Todo, Skills (`concurrent-research`), FileMemory, FileAccess, Background, Compaction, ToolApproval — all on (listed in manifest).

### Domain tools

Pipeline tool + BackgroundAgents protocol tools; parent/child may use MCP search. Leaf `AngleResearcher` has no full Harness; parent orchestrates.

### Memory & context

FileMemory notes, FileAccess exports (writes approved), Workflow checkpoints (`resume=true`), compaction on long threads.

### Safety & limits

No invented citations; parallel start-all-then-wait (never serial start→wait→start). MCP loads at process start; stdio cwd follows startup `ORBIT_WORKSPACE` (restart agent after workspace change). Workspace is optional.

---

## Coding Assistant

**Manifest**: `agents/coder.agent.json` · Project: `Orbit.Agents.Coder`

### Purpose

Mini Claude Code shape in the host **Workspace**: read/edit files, approved Git Bash for build/test/git.

### Harness

Mode, Todo, Skills (`minimal-diff`), FileMemory, FileAccess, Compaction, ToolApproval — on; Background off.

### Domain tools

FileAccess read/write tools, approved `bash` (Git Bash only; `ORBIT_BASH_PATH` override; **no PowerShell fallback**), `get_coder_workspace`, `file_memory_*` for compaction-surviving notes.

### Memory & context

FileMemory for working notes (not a substitute for repo files). FileAccess root = session workspace snapshot. Compaction still applies.

### Safety & limits

Bash allowlist + path fence + Job Object (**not** a full sandbox).  
**Product stance (0.0.1):** allowlisting `node` / `python` / `npm` / `pip` / `dotnet` means **user-level arbitrary code execution inside the workspace**. No AppContainer in this version — **only open trusted workspaces**. Alternatives later: drop interpreters from the allowlist, or real isolation (`SECURITY.md`).  
Workspace required; missing Git Bash fails closed; workspace switch may cancel runs and restart the agent.

---

## Cross-cutting

### Compaction

`ContextWindowCompactionStrategy` in the chat pipeline and history reducer when `compaction` is in `harness[]`. Shared defaults via `OrbitLlmContextWindow`. All three built-in agents list it explicitly.

### Memory kinds

| Kind | Agents | Store | Role |
|------|--------|-------|------|
| Chat / AgentSession | All | `.agent.json` + session JSONL | Multi-turn resume |
| FileMemory | All | `data/file-memory/{id}/` | Compaction-surviving notes |
| FileAccess | Research, Coder | Workspace / shared disk | Durable files |
| Browser profile | Mail | `data/browser-profiles/...` | Login cookies |

### Skills

Per-agent `skills/<id>/SKILL.md`; catalog in prompt, body via `load_skill`. Read-only skill tools may auto-approve.

### Tool approval

Framework HITL; Runtime only resumes interrupts (`source: tool`). Shell shows approval cards and trajectory events.

### Trajectory vs logs

Trajectory = session event tree for replay. Logs = `%LOCALAPPDATA%/Orbit/logs/` (`runtime-*.log` / `agent-*.log` / `shell-*.log`, Serilog).

---

## Adding an agent

1. `agents/{id}.agent.json` (`harness`, `workspace`)  
2. `src/Orbit.Agents.{Name}/` + optional `skills/`  
3. Document a section here (+ Chinese)  
4. Keep domain DTOs out of Runtime; mutations go through tool approval  
