# Contributing to Orbit

## Prerequisites

- .NET SDK matching `global.json`
- Windows recommended for Shell / Playwright / Git Bash paths (CI is `windows-latest`)

## Build & test

```bash
dotnet restore Orbit.slnx -p:Configuration=Release
dotnet build Orbit.slnx -c Release --no-restore
dotnet test Orbit.slnx -c Release --no-build
dotnet format Orbit.slnx --verify-no-changes --no-restore --severity warn
```

## Conventions

- **Framework first**: prefer MAF / MEAI Harness providers over bespoke agent loops. Orbit owns the host (Runtime, session tree, Workspace), not a parallel agent runtime.
- **C# comments / XML docs**: English only.
- **User-facing strings**: Shell i18n (`strings.zh.json` / `strings.en.json`). Do not sprinkle product “MAF” branding in UI.
- **Domain boundaries**: keep mail/coder/research DTOs out of Runtime; mutations go through tool approval.
- **Tests**: put pure logic under Contracts / small helpers so it can be tested without Avalonia. Symlink/junction coverage must not silently skip on CI (`CI` env → fail if links cannot be created).

## Docs

| Doc | Role |
|-----|------|
| [README](README.md) / [README-zh](README-zh.md) | Getting started |
| [docs/architecture.md](docs/architecture.md) | Process model |
| [docs/agents.md](docs/agents.md) | Per-agent capabilities |
| [CHANGELOG.md](CHANGELOG.md) | User-visible changes |
| [SECURITY.md](SECURITY.md) | Vulnerability reporting |

## Pull requests

- Keep PRs focused; include test updates for behavioral changes.
- Do not commit secrets, `data/sessions`, API keys, or `%LOCALAPPDATA%/Orbit` contents.
