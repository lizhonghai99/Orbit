---
name: concurrent-research
description: Concurrent BackgroundAgents protocol + when to call the Workflow pipeline.
---

# Concurrent research

## BackgroundAgents (must)

1. For each angle / ticker / sub-question: `background_agents_start_task` with `agentName=AngleResearcher`.
2. **Start all tasks before any wait.**
3. Loop `background_agents_wait_for_first_completion` until done.
4. `background_agents_get_task_results` → optional `continue_task` → Markdown summary/table.
5. `background_agents_clear_completed_task`.

Never serial start→wait→start.

## Workflow

After probes (or for a single deep brief): `run_research_pipeline(topic, focus?)`  
(Scope → Outline → Draft, file checkpoints).
