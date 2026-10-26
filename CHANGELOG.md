# Changelog

All notable changes to Orbit are documented here. Format inspired by [Keep a Changelog](https://keepachangelog.com/).

## [0.0.1] — 2026-10

### Added

- Runtime + Avalonia Shell + Mail / Research / Coding agents (AG-UI)
- Host Workspace, tool-approval HITL, session JSONL + trajectory
- Serilog rolling logs under `%LOCALAPPDATA%/Orbit/logs/`
- Per-agent capability docs (`docs/agents.md`)
- Session incremental sync: `GET /sessions/{id}?after={entryId}` (`SessionSyncResponse`)
- Session SSE: `GET /sessions/{id}/events` (Shell uses during turns)
- CI MTP code coverage (`--coverage` / cobertura artifact); browser-profiles ops doc

### Security

- Coding Assistant: Git Bash only (no PowerShell fallback); path fence via `OrbitPathNames.IsUnderOrEqual`
- Explicit stance: interpreter allowlist ⇒ trusted workspaces only (not a sandbox)

### Fixed

- CI `dotnet format` severity; Release restore vs Debug Shell assets
- Approval Runtime no longer interprets mail domain payloads
- Unknown HTTP faults → logged generic 500 (not client 400 with internal messages)
- Session HTTP tests host production `MapSessionSyncEndpoints` (incl. SSE sync/ping); heartbeat decoupled from poll
- Shell SSE watch: cancel → await → dispose; conversation list applies projected VMs incrementally
- `JsonlSessionStore` live-cache capped; `GetSync` cursor index cached; `SessionEntrySync.MergeEntries` O(n+m)
- `OrbitLocalAuth` pid claim testable via directory; `Orbit.Agents.Coder.Tests` split from Hosting tests
- AG-UI `HttpClient.Timeout` no longer hard-caps at 5 min (was cutting Research’s 10 min budget); optional `ORBIT_TURN_TIMEOUT_MINUTES`
- Approval decisions: unknown values rejected on both tool and legacy cards (no default-approve)
- `CancelAndDrainAsync` returns bool → delete/archive 409 when turn does not drain; `_runGates` released on session delete
- Attachments: `IsSafeAgentId` + `SanitizeFileName` (ADS-safe); MCP `approvalTools` for per-tool HITL
- Research pipeline stages → `research_stage` meta via agent-meta route (Shell SSE); Mail Archive button regex covers common locales
