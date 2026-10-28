---
name: long-running
description: Long-Running path — FeatureList + run_coding_turn context reset (fresh Generator/Evaluator sessions).
---

# Routes

| Route | Chain |
|-------|--------|
| **Short-Running** | `coder_router` → `coder_generator` → `coder_evaluator` (HITL handoff) |
| **Long-Running** | `coder_router` → `coder_planner` → tools only |

| Id | Role |
|----|------|
| `coder_router` | Pick Short-Running vs Long-Running |
| `coder_planner` | `run_feature_list` (resume/reset) + **`run_coding_turn`** (approval → context reset) |
| `coder_generator` | Short-Running implement (HITL); Long-Running nested in coding turn |
| `coder_evaluator` | Short-Running QA (HITL); Long-Running nested (**read-only** FileAccess) |

## Context reset (Long-Running)

Each `run_coding_turn` (requires user approval):

1. Claim `ready` → `in_progress` (stuck `in_progress` / exhausted `codingTurns` auto-fail)
2. **New** Generator `AgentSession` seeded only from the FeatureList brief
3. **New** Evaluator `AgentSession` seeded from brief + Generator report (15‑min turn timeout)
4. Outer AG-UI chat is **not** their context (anti Context Anxiety)

`run_feature_list`: existing list → `resume=true` or `reset=true` (reset **archives** old files; confirm with user first).  
`done` notes must contain `VERDICT: PASS`. Caps: Attempt ≥ 3, MaxCodingTurns per feature, ready WIP.

Repeat: `run_feature_list(resume=true)` → `run_coding_turn` until done → handoff `coder_router`.

## Short-Running stops

- FailGate: advisory, scoped per AG-UI **RunId** (not SessionId)
- Handoff graph: ~80 messages hard-ish outer cap for that run

Tools: `run_feature_list`, `run_coding_turn`, `get_feature_list`, `set_feature_status`.

Store: `data/feature-lists/coder/`.
