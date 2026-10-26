---
name: outlook-triage
description: Outlook Web inbox triage — login, list, suggest archive/folders/moves, HITL approval.
---

Browser is headless by default; a visible window opens only on a login wall.

1. `get_browser_status` / `go_to_inbox` — go to inbox when already signed in  
2. If `loginWall` / not signed in → `open_login_page` → `wait_for_url_change`  
3. `list_inbox_mails` first; show the tool `markdown` table (with [Open] links) to the user  
4. After the user agrees to organize → `organize_inbox` (read-only plan in markdown)  
5. Mutations (each needs tool approval):  
   - `create_folder`  
   - `move_mail`  
   - `archive_mail` (never delete)  
6. One approval at a time; do not claim created/moved/archived before approval  

## Reply format

- Markdown tables/lists only — no raw JSON dumps.  
- Link readable mail as `[Open](url)`; if no url, say to open the row in Outlook.  
- When recommending reads, cite why (unread, subject keywords, etc.).  
