---
name: minimal-diff
description: Prefer smallest reviewable edits; read before write; summarize after.
---

# Minimal diff

1. `file_access_ls` / `file_access_grep` / `file_access_read` before editing.
2. Prefer `file_access_replace` (exact old→new) over rewriting whole files.
3. One concern per change set; use `todos_*` if multi-file.
4. After tools succeed, summarize paths touched and why — no invented success claims without tool results.
