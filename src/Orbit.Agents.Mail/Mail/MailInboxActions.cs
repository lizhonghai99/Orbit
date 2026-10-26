using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Orbit.Contracts;

namespace Orbit.Agents.Mail.Mail;

/// <summary>
/// Outlook Web–oriented inbox helpers used by the mail agent.
/// Destructive deletes are never automatic. Archive / create folder / move run only after HITL approve.
/// </summary>
public static partial class MailInboxActions
{
    private static readonly JsonSerializerOptions InboxJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool LooksLikeOutlook(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.Contains("outlook.live.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("outlook.office.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("outlook.office365.com", StringComparison.OrdinalIgnoreCase));

    public static string InboxUrlFrom(string loginOrAnyUrl)
    {
        if (Uri.TryCreate(loginOrAnyUrl, UriKind.Absolute, out var uri))
        {
            if (uri.Host.Contains("outlook.office", StringComparison.OrdinalIgnoreCase))
            {
                return $"{uri.Scheme}://{uri.Host}/mail/";
            }
        }

        return "https://outlook.live.com/mail/0/";
    }

    public static async Task<string> GoToInboxAsync(IPage page, string defaultLoginUrl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = InboxUrlFrom(string.IsNullOrWhiteSpace(page.Url) ? defaultLoginUrl : page.Url);
        await page.GotoAsync(target, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000
        }).ConfigureAwait(false);

        // Prefer list hydration over a fixed sleep (slow networks / localized chrome).
        await WaitForInboxListAsync(page).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            action = "go_to_inbox",
            url = page.Url,
            title = await page.TitleAsync().ConfigureAwait(false),
            hint = LooksLikeOutlook(page.Url)
                ? "Opened Outlook mail. If still on a login page, sign in first, then list_inbox_mails or organize_inbox."
                : "Navigated; URL does not look like Outlook — organize will try a generic list scrape."
        });
    }

    public static async Task<List<MailListItem>> ExtractInboxAsync(IPage page, int maxItems, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        maxItems = Math.Clamp(maxItems, 5, 50);

        var raw = await page.EvaluateAsync<string>("""
            (maxItems) => {
              const origin = location.origin || 'https://outlook.live.com';
              const seen = new Set();
              const items = [];

              const clean = (s) => (s || '').trim().replace(/\s+/g, ' ');
              const resolveHref = (el) => {
                if (!el) return null;
                const a = el.closest?.('a[href]') || el.querySelector?.('a[href]');
                if (a && a.href && !a.href.startsWith('javascript:')) return a.href;
                const id = el.getAttribute?.('data-convid')
                  || el.getAttribute?.('data-itemid')
                  || el.getAttribute?.('data-id')
                  || el.getAttribute?.('data-listitemid');
                if (id) {
                  const enc = encodeURIComponent(id);
                  return origin + '/mail/0/id/' + enc;
                }
                // Some list rows expose a child with id-like attributes.
                const child = el.querySelector?.('[data-convid],[data-itemid],[data-id]');
                if (child) {
                  const cid = child.getAttribute('data-convid')
                    || child.getAttribute('data-itemid')
                    || child.getAttribute('data-id');
                  if (cid) return origin + '/mail/0/id/' + encodeURIComponent(cid);
                }
                return null;
              };

              const push = (subject, from, href, label, unread) => {
                const sub = clean(subject);
                const fr = clean(from);
                const text = (fr && sub) ? (fr + ' — ' + sub) : (sub || fr || clean(label));
                if (!text || text.length < 4 || text.length > 220) return;
                const key = text.toLowerCase();
                if (seen.has(key)) return;
                seen.add(key);
                items.push({
                  subject: sub || null,
                  from: fr || null,
                  href: href || null,
                  text,
                  unread: !!unread,
                  kind: 'mail'
                });
              };

              // en-US Outlook chrome only (browser Locale forced); subject text may still be Chinese.
              const parseLabel = (label) => {
                let from = null, subject = null;
                const fromMatch = label.match(/(?:from)\s+(.+?)(?:\s+(?:Subject)\s+|$)/i);
                if (fromMatch) from = fromMatch[1];
                const subMatch = label.match(/(?:Subject)\s+(.+?)(?:\s+(?:Received|Flagged|Categorized)|$)/i);
                if (subMatch) subject = subMatch[1];
                if (!subject && !from) {
                  subject = label.length > 100 ? label.slice(0, 100) : label;
                }
                const unread = /unread/i.test(label);
                return { from, subject, unread };
              };

              for (const el of document.querySelectorAll('[role="option"], [role="listitem"], [data-convid]')) {
                const label = clean(el.getAttribute('aria-label') || el.innerText || '');
                if (!label || label.length < 8) continue;
                if (label.length < 24 && /^(Inbox|Drafts|Sent|Deleted|Junk)/i.test(label)) continue;
                const parsed = parseLabel(label);
                push(parsed.subject, parsed.from, resolveHref(el), label, parsed.unread);
                if (items.length >= maxItems) break;
              }

              if (items.length < Math.min(5, maxItems)) {
                for (const a of document.querySelectorAll('a[href*="/mail/"]')) {
                  const t = clean(a.innerText || a.textContent);
                  if (!t || t.length < 4) continue;
                  push(t, null, a.href, t, /unread/i.test(t));
                  if (items.length >= maxItems) break;
                }
              }

              return JSON.stringify(items.slice(0, maxItems));
            }
            """, maxItems).ConfigureAwait(false);

        return JsonSerializer.Deserialize<List<MailListItem>>(raw ?? "[]", InboxJsonOptions) ?? [];
    }

    /// <summary>Markdown table with open links — Shell renders this; models should reuse it.</summary>
    public static string BuildInboxMarkdown(IReadOnlyList<MailListItem> items)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| # | Mail | Status | Open |");
        sb.AppendLine("|---|---|---|---|");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var title = EscapeMdCell(string.IsNullOrWhiteSpace(item.Text)
                ? (item.Subject ?? item.From ?? "(untitled)")
                : item.Text);
            var status = item.Unread == true ? "Unread" : "—";
            var link = string.IsNullOrWhiteSpace(item.Href)
                ? "—"
                : $"[Open]({item.Href})";
            sb.AppendLine($"| {i + 1} | {title} | {status} | {link} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string EscapeMdCell(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");

    public static string BuildOrganizeJson(
        IReadOnlyList<MailListItem> items,
        string? url,
        string? title,
        bool usedDemo,
        string hint)
    {
        // Drop rows with no readable label — empty approvals are useless HITL noise.
        var usable = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Text)
                        || !string.IsNullOrWhiteSpace(i.Subject)
                        || !string.IsNullOrWhiteSpace(i.From))
            .Select(NormalizeItem)
            .ToList();

        var proposals = usable.Select((item, index) =>
        {
            var suggestion = SuggestMailAction(item);
            return new
            {
                id = $"mail-{index + 1}",
                text = item.Text,
                subject = item.Subject,
                from = item.From,
                href = item.Href,
                suggestedAction = suggestion.Action,
                targetFolder = suggestion.TargetFolder,
                reason = suggestion.Reason,
                requiresApproval = true
            };
        }).ToList();

        var markdown = usable.Count == 0
            ? ""
            : BuildInboxMarkdown(usable) + "\n\n> Approve one at a time; open suggested mail via the Open column.";

        return JsonSerializer.Serialize(new
        {
            ok = true,
            action = "organize_inbox",
            provider = LooksLikeOutlook(url) ? "outlook" : "generic",
            url,
            title,
            count = proposals.Count,
            scrapedCount = items.Count,
            usedDemoContent = usedDemo,
            started = url is not null,
            requiresApproval = proposals.Count > 0,
            executedDeletes = 0,
            markdown,
            hint = proposals.Count == 0 && items.Count > 0
                ? "List rows found but subject/from empty; refresh the inbox and try again."
                : hint,
            proposals
        });
    }

    private static MailListItem NormalizeItem(MailListItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            return item;
        }

        var text = !string.IsNullOrWhiteSpace(item.From) && !string.IsNullOrWhiteSpace(item.Subject)
            ? $"{item.From} — {item.Subject}"
            : item.Subject ?? item.From ?? "";
        item.Text = text.Trim();
        return item;
    }

    public static async Task<MailMutationResult> ApplyMailDecisionAsync(
        IPage page,
        MailMutationRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var action = request.SuggestedAction?.Trim() ?? "";

        if (string.Equals(action, "keep_important", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "review", StringComparison.OrdinalIgnoreCase))
        {
            return new MailMutationResult
            {
                Ok = true,
                Executed = true,
                Mode = "record_only",
                Message = $"Recorded '{action}'; mailbox unchanged.",
                Url = page.Url
            };
        }

        if (string.Equals(action, "create_folder", StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyCreateFolderAsync(page, request).ConfigureAwait(false);
        }

        if (string.Equals(action, "move_to_folder", StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyMoveToFolderAsync(page, request).ConfigureAwait(false);
        }

        if (!string.Equals(action, "archive_or_delete", StringComparison.OrdinalIgnoreCase))
        {
            return new MailMutationResult
            {
                Ok = false,
                Executed = false,
                Mode = "unsupported",
                Message = $"Unsupported action: {action}",
                Error = "unsupported_action"
            };
        }

        if (IsDemoHref(request.Href))
        {
            return new MailMutationResult
            {
                Ok = true,
                Executed = false,
                Mode = "demo_skipped",
                Message = "Demo item approved; skipped real action. Sign in to Outlook inbox before organizing.",
                Url = page.Url
            };
        }

        // Empty href is not demo — locate by subject/text (TryOpenMailAsync). Missing both → fail.
        if (string.IsNullOrWhiteSpace(request.Href)
            && string.IsNullOrWhiteSpace(request.Text)
            && string.IsNullOrWhiteSpace(request.ProposalId))
        {
            return new MailMutationResult
            {
                Ok = false,
                Executed = false,
                Mode = "missing_target",
                Message = "Missing mail link or subject; cannot archive.",
                Error = "missing_mail_target",
                Url = page.Url
            };
        }

        var opened = await TryOpenMailAsync(page, request).ConfigureAwait(false);
        if (!opened.Ok)
        {
            return opened;
        }

        // Do not Archive whatever is currently selected when the target was not uniquely opened.
        if (opened.Executed == false
            || string.Equals(opened.Mode, "not_found", StringComparison.OrdinalIgnoreCase)
            || string.Equals(opened.Mode, "ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            return opened;
        }

        // Prefer Archive (reversible); never click Delete.
        var archived = await TryClickArchiveAsync(page).ConfigureAwait(false);
        if (archived)
        {
            return new MailMutationResult
            {
                Ok = true,
                Executed = true,
                Mode = "archived_ui",
                Message = "Opened mail and clicked Archive. Delete was not used.",
                Url = page.Url
            };
        }

        return new MailMutationResult
        {
            Ok = true,
            Executed = false,
            Mode = "archive_ui_not_found",
            Message = "Opened mail but Archive button not found. Archive/delete manually in the browser (Orbit never auto-deletes).",
            Url = page.Url
        };
    }

    private static async Task<MailMutationResult> ApplyCreateFolderAsync(
        IPage page,
        MailMutationRequest request)
    {
        var folder = request.TargetFolder?.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new MailMutationResult
            {
                Ok = false,
                Executed = false,
                Mode = "missing_folder",
                Message = "Missing target folder name.",
                Error = "missing_target_folder",
                Url = page.Url
            };
        }

        if (IsDemoHref(request.Href) && string.IsNullOrWhiteSpace(request.Text))
        {
            // create_folder proposals often have no href; still allow real UI create.
        }

        try
        {
            var created = await TryCreateFolderAsync(page, folder).ConfigureAwait(false);
            if (created)
            {
                return new MailMutationResult
                {
                    Ok = true,
                    Executed = true,
                    Mode = "folder_created",
                    Message = $"Attempted to create folder '{folder}'.",
                    Url = page.Url
                };
            }

            return new MailMutationResult
            {
                Ok = true,
                Executed = false,
                Mode = "folder_ui_not_found",
                Message = $"Could not find 'Create new folder'. Create '{folder}' manually in the Outlook folder tree.",
                Url = page.Url
            };
        }
        catch (Exception ex)
        {
            return new MailMutationResult
            {
                Ok = false,
                Executed = false,
                Mode = "create_folder_failed",
                Message = "Failed to create folder.",
                Error = ex.Message,
                Url = page.Url
            };
        }
    }

    private static async Task<MailMutationResult> ApplyMoveToFolderAsync(
        IPage page,
        MailMutationRequest request)
    {
        var folder = request.TargetFolder?.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new MailMutationResult
            {
                Ok = false,
                Executed = false,
                Mode = "missing_folder",
                Message = "Missing target folder name.",
                Error = "missing_target_folder",
                Url = page.Url
            };
        }

        if (IsDemoHref(request.Href))
        {
            return new MailMutationResult
            {
                Ok = true,
                Executed = false,
                Mode = "demo_skipped",
                Message = $"Demo item approved for move to '{folder}'; skipped real action.",
                Url = page.Url
            };
        }

        var opened = await TryOpenMailAsync(page, request).ConfigureAwait(false);
        if (!opened.Ok)
        {
            return opened;
        }

        if (string.Equals(opened.Mode, "not_found", StringComparison.OrdinalIgnoreCase)
            && opened.Executed == false)
        {
            return opened;
        }

        try
        {
            var moved = await TryMoveToFolderAsync(page, folder).ConfigureAwait(false);
            if (moved)
            {
                return new MailMutationResult
                {
                    Ok = true,
                    Executed = true,
                    Mode = "moved_ui",
                    Message = $"Opened mail and attempted move to '{folder}'.",
                    Url = page.Url
                };
            }

            return new MailMutationResult
            {
                Ok = true,
                Executed = false,
                Mode = "move_ui_not_found",
                Message = $"Opened mail but move to '{folder}' did not complete. Use Move to in the browser.",
                Url = page.Url
            };
        }
        catch (Exception ex)
        {
            return new MailMutationResult
            {
                Ok = false,
                Executed = false,
                Mode = "move_failed",
                Message = "Failed to move mail.",
                Error = ex.Message,
                Url = page.Url
            };
        }
    }

    private static async Task<bool> TryCreateFolderAsync(IPage page, string folderName)
    {
        // Outlook Web (en-US): folder pane "Create new folder", then name dialog.
        var createTriggers = new[]
        {
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = CreateFolderRegex() }),
            page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { NameRegex = CreateFolderRegex() }),
            page.Locator("[aria-label*='Create new folder' i], [title*='Create new folder' i]")
        };

        foreach (var trigger in createTriggers)
        {
            try
            {
                if (await trigger.CountAsync().ConfigureAwait(false) == 0)
                {
                    continue;
                }

                await trigger.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
                var input = page.GetByRole(AriaRole.Textbox).Last;
                try
                {
                    await input.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 3_000
                    }).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (await input.CountAsync().ConfigureAwait(false) > 0)
                {
                    await input.FillAsync(folderName).ConfigureAwait(false);
                    await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
                    await SettleUiAsync(page).ConfigureAwait(false);
                    return true;
                }
            }
            catch
            {
                // try next selector
            }
        }

        // Context menu on "Folders" root.
        try
        {
            var foldersRoot = page.GetByRole(AriaRole.Treeitem, new PageGetByRoleOptions
            {
                NameRegex = new Regex("Folders", RegexOptions.IgnoreCase)
            });
            if (await foldersRoot.CountAsync().ConfigureAwait(false) > 0)
            {
                await foldersRoot.First.ClickAsync(new LocatorClickOptions
                {
                    Button = MouseButton.Right,
                    Timeout = 5_000
                }).ConfigureAwait(false);
                var menu = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { NameRegex = CreateFolderRegex() });
                try
                {
                    await menu.First.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 2_000
                    }).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return false;
                }

                if (await menu.CountAsync().ConfigureAwait(false) > 0)
                {
                    await menu.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
                    var input = page.GetByRole(AriaRole.Textbox).Last;
                    try
                    {
                        await input.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Visible,
                            Timeout = 3_000
                        }).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        return false;
                    }

                    if (await input.CountAsync().ConfigureAwait(false) > 0)
                    {
                        await input.FillAsync(folderName).ConfigureAwait(false);
                        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
                        await SettleUiAsync(page).ConfigureAwait(false);
                        return true;
                    }
                }
            }
        }
        catch
        {
            // leave for manual
        }

        return false;
    }

    private static async Task<bool> TryMoveToFolderAsync(IPage page, string folderName)
    {
        try
        {
            var moveBtn = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = MoveButtonRegex() });
            if (await moveBtn.CountAsync().ConfigureAwait(false) == 0)
            {
                moveBtn = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { NameRegex = MoveButtonRegex() });
            }

            if (await moveBtn.CountAsync().ConfigureAwait(false) > 0)
            {
                await moveBtn.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
            }
            else
            {
                // Outlook shortcut often opens Move dialog.
                await page.Keyboard.PressAsync("Control+Shift+V").ConfigureAwait(false);
            }

            await SettleUiAsync(page, timeoutMs: 2_000).ConfigureAwait(false);

            var folderItem = page.GetByRole(AriaRole.Option, new PageGetByRoleOptions
            {
                NameRegex = new Regex(Regex.Escape(folderName), RegexOptions.IgnoreCase)
            });
            if (await folderItem.CountAsync().ConfigureAwait(false) == 0)
            {
                folderItem = page.GetByRole(AriaRole.Treeitem, new PageGetByRoleOptions
                {
                    NameRegex = new Regex(Regex.Escape(folderName), RegexOptions.IgnoreCase)
                });
            }

            if (await folderItem.CountAsync().ConfigureAwait(false) == 0)
            {
                folderItem = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions
                {
                    NameRegex = new Regex(Regex.Escape(folderName), RegexOptions.IgnoreCase)
                });
            }

            // Type-ahead filter in Move dialog.
            if (await folderItem.CountAsync().ConfigureAwait(false) == 0)
            {
                var search = page.GetByRole(AriaRole.Textbox).Last;
                if (await search.CountAsync().ConfigureAwait(false) > 0)
                {
                    await search.FillAsync(folderName).ConfigureAwait(false);
                    folderItem = page.GetByRole(AriaRole.Option, new PageGetByRoleOptions
                    {
                        NameRegex = new Regex(Regex.Escape(folderName), RegexOptions.IgnoreCase)
                    });
                    try
                    {
                        await folderItem.First.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Visible,
                            Timeout = 2_000
                        }).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // fall through — Count check below
                    }
                }
            }

            if (await folderItem.CountAsync().ConfigureAwait(false) > 0)
            {
                await folderItem.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
                await SettleUiAsync(page, timeoutMs: 2_000).ConfigureAwait(false);

                var confirm = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                {
                    NameRegex = new Regex("^(Move|OK)$", RegexOptions.IgnoreCase)
                });
                if (await confirm.CountAsync().ConfigureAwait(false) > 0)
                {
                    await confirm.First.ClickAsync(new LocatorClickOptions { Timeout = 3_000 }).ConfigureAwait(false);
                }

                return true;
            }
        }
        catch
        {
            // leave for manual
        }

        return false;
    }

    private static async Task<MailMutationResult> TryOpenMailAsync(IPage page, MailMutationRequest request)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(request.Href) && !IsDemoHref(request.Href))
            {
                if (!NavigationAllowlist.IsAllowed(request.Href, MailBrowserPolicy.AllowedHosts))
                {
                    return new MailMutationResult
                    {
                        Ok = false,
                        Executed = false,
                        Mode = "href_blocked",
                        Message = "Link host is not on the Outlook allowlist; navigation refused.",
                        Error = "href_not_allowed",
                        Url = page.Url
                    };
                }

                await page.GotoAsync(request.Href, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000
                }).ConfigureAwait(false);
                await SettleUiAsync(page).ConfigureAwait(false);
                var hrefSubject = ExtractSubjectNeedle(request.Text ?? request.ProposalId ?? "");
                if (!string.IsNullOrWhiteSpace(hrefSubject)
                    && !await ReadingPaneMatchesSubjectAsync(page, hrefSubject).ConfigureAwait(false))
                {
                    return new MailMutationResult
                    {
                        Ok = true,
                        Executed = false,
                        Mode = "not_found",
                        Message =
                            "Opened href but the reading-pane subject does not match the approved mail; archive skipped.",
                        Url = page.Url
                    };
                }

                return new MailMutationResult { Ok = true, Executed = true, Mode = "opened_href", Url = page.Url };
            }

            // Click list row by subject — exact single match only (never .First on a prefix).
            var needle = request.Text ?? request.ProposalId ?? "";
            if (!string.IsNullOrWhiteSpace(needle))
            {
                var subject = ExtractSubjectNeedle(needle);
                var subjectRegex = BuildExactSubjectRegex(subject);
                var option = page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { NameRegex = subjectRegex });
                var optionCount = await option.CountAsync().ConfigureAwait(false);
                if (optionCount > 1)
                {
                    return new MailMutationResult
                    {
                        Ok = true,
                        Executed = false,
                        Mode = "ambiguous",
                        Message =
                            $"Multiple inbox rows match subject ({optionCount}); open the mail manually so the wrong thread is not archived.",
                        Url = page.Url
                    };
                }

                if (optionCount == 1)
                {
                    await option.First.ClickAsync(new LocatorClickOptions { Timeout = 8_000 }).ConfigureAwait(false);
                    await SettleUiAsync(page).ConfigureAwait(false);
                    if (!await ReadingPaneMatchesSubjectAsync(page, subject).ConfigureAwait(false))
                    {
                        return new MailMutationResult
                        {
                            Ok = true,
                            Executed = false,
                            Mode = "not_found",
                            Message =
                                "Opened a row but the reading-pane subject does not match the approved mail; archive skipped.",
                            Url = page.Url
                        };
                    }

                    return new MailMutationResult { Ok = true, Executed = true, Mode = "clicked_row", Url = page.Url };
                }

                var link = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameRegex = subjectRegex });
                var linkCount = await link.CountAsync().ConfigureAwait(false);
                if (linkCount > 1)
                {
                    return new MailMutationResult
                    {
                        Ok = true,
                        Executed = false,
                        Mode = "ambiguous",
                        Message =
                            $"Multiple links match subject ({linkCount}); open the mail manually so the wrong thread is not archived.",
                        Url = page.Url
                    };
                }

                if (linkCount == 1)
                {
                    await link.First.ClickAsync(new LocatorClickOptions { Timeout = 8_000 }).ConfigureAwait(false);
                    await SettleUiAsync(page).ConfigureAwait(false);
                    if (!await ReadingPaneMatchesSubjectAsync(page, subject).ConfigureAwait(false))
                    {
                        return new MailMutationResult
                        {
                            Ok = true,
                            Executed = false,
                            Mode = "not_found",
                            Message =
                                "Opened a link but the reading-pane subject does not match the approved mail; archive skipped.",
                            Url = page.Url
                        };
                    }

                    return new MailMutationResult { Ok = true, Executed = true, Mode = "clicked_link", Url = page.Url };
                }
            }

            return new MailMutationResult
            {
                Ok = true,
                Executed = false,
                Mode = "not_found",
                Message = "Could not locate that mail in the inbox; open it manually.",
                Url = page.Url
            };
        }
        catch (Exception ex)
        {
            return new MailMutationResult
            {
                Ok = false,
                Executed = false,
                Mode = "apply_failed",
                Message = "Failed to open mail.",
                Error = ex.Message,
                Url = page.Url
            };
        }
    }

    private static async Task<bool> TryClickArchiveAsync(IPage page)
    {
        try
        {
            var archive = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                NameRegex = ArchiveButtonRegex()
            });
            if (await archive.CountAsync().ConfigureAwait(false) > 0)
            {
                await archive.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
                await SettleUiAsync(page, timeoutMs: 2_000).ConfigureAwait(false);
                return true;
            }

            // Toolbar menuitem fallback.
            var item = page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions
            {
                NameRegex = ArchiveButtonRegex()
            });
            if (await item.CountAsync().ConfigureAwait(false) > 0)
            {
                await item.First.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
                return true;
            }
        }
        catch
        {
            // leave for manual
        }

        return false;
    }

    private static (string Action, string Reason, string? TargetFolder) SuggestMailAction(MailListItem item)
    {
        var t = $"{item.From} {item.Subject} {item.Text}".ToLowerInvariant();
        // Subject/body heuristics keep CN needles; DOM chrome is en-US only.
        if (ContainsAny(t, "unsubscribe", "退订", "广告", "促销", "优惠", "promo", "sale", "newsletter", "订阅", "marketing", "deal", "coupon"))
        {
            return ("archive_or_delete", "Likely marketing/newsletter → suggest archive", null);
        }

        if (ContainsAny(t, "invoice", "账单", "发票", "receipt", "payment", "还款", "银行", "工资", "payroll", "tax"))
        {
            return ("move_to_folder", "Likely finance/billing → suggest move to Finance", "Finance");
        }

        if (ContainsAny(t, "verify", "验证", "security", "登录通知", "unusual", "password", "密码", "2fa", "verification"))
        {
            return ("keep_important", "Likely security notice → keep", null);
        }

        return ("review", "Open and review before deciding", null);
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.Ordinal));

    /// <summary>Only explicit demo hosts — empty href means "locate by text", not demo skip.</summary>
    private static bool IsDemoHref(string? href) =>
        !string.IsNullOrWhiteSpace(href)
        && href.Contains("example.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Subject after em dash (from "From — Subject" list labels), otherwise full needle.</summary>
    private static string ExtractSubjectNeedle(string needle)
    {
        var s = needle.Contains('—', StringComparison.Ordinal) ? needle.Split('—')[^1].Trim() : needle.Trim();
        return s;
    }

    /// <summary>
    /// Accessible-name match for a list row: subject must appear as a whole token sequence
    /// (escaped, case-insensitive). Not a free-floating 40-char prefix.
    /// </summary>
    private static Regex BuildExactSubjectRegex(string subject)
    {
        var escaped = Regex.Escape(subject.Trim());
        return new Regex(
            $@"(?:^|[\s\|·•\-–—]){escaped}(?:$|[\s\|·•\-–—])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>Strip Re:/Fw:/回复: noise and collapse whitespace for equality checks.</summary>
    public static string NormalizeSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "";
        }

        var s = subject.Trim();
        while (true)
        {
            var next = SubjectPrefixRegex().Replace(s, "");
            if (string.Equals(next, s, StringComparison.Ordinal))
            {
                break;
            }

            s = next.Trim();
        }

        return WhitespaceRegex().Replace(s, " ").Trim();
    }

    /// <summary>
    /// Archive safety: normalized subjects equal, or the pane text starts with the expected stem.
    /// Deliberately not bidirectional Contains (want="Invoice" must not accept got="Your Invoice …").
    /// </summary>
    public static bool SubjectsMatchForArchive(string? expectedSubject, string? actualSubject)
    {
        var want = NormalizeSubject(expectedSubject);
        if (want.Length == 0)
        {
            return false;
        }

        var got = NormalizeSubject(actualSubject);
        return string.Equals(got, want, StringComparison.OrdinalIgnoreCase)
               || got.StartsWith(want, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ReadingPaneMatchesSubjectAsync(IPage page, string expectedSubject)
    {
        if (NormalizeSubject(expectedSubject).Length == 0)
        {
            return false;
        }

        // Outlook reading pane: heading / aria subject region (en-US + generic).
        var candidates = new[]
        {
            page.GetByRole(AriaRole.Heading).First,
            page.Locator("[aria-label*='Subject' i], [data-app-section='ConversationReadingPane'] h1, [data-app-section='ConversationReadingPane'] h2").First
        };

        foreach (var loc in candidates)
        {
            try
            {
                if (await loc.CountAsync().ConfigureAwait(false) == 0)
                {
                    continue;
                }

                var text = (await loc.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 2_000 })
                    .ConfigureAwait(false))?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (SubjectsMatchForArchive(expectedSubject, text))
                {
                    return true;
                }
            }
            catch (TimeoutException)
            {
            }
            catch (PlaywrightException)
            {
            }
        }

        // No readable heading — refuse archive rather than guess.
        return false;
    }

    /// <summary>Wait for inbox list rows when present; otherwise fall back to load-state settle.</summary>
    private static async Task WaitForInboxListAsync(IPage page)
    {
        try
        {
            await page.GetByRole(AriaRole.Option).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000
            }).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            // Login wall / empty inbox / non-Outlook — settle anyway.
        }

        await SettleUiAsync(page).ConfigureAwait(false);
    }

    /// <summary>Prefer NetworkIdle/DOMContentLoaded over fixed sleeps after clicks/navigation.</summary>
    private static async Task SettleUiAsync(IPage page, int timeoutMs = 3_000)
    {
        try
        {
            await page.WaitForLoadStateAsync(
                    LoadState.NetworkIdle,
                    new PageWaitForLoadStateOptions { Timeout = timeoutMs })
                .ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }

        try
        {
            await page.WaitForLoadStateAsync(
                    LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = Math.Min(1_500, timeoutMs) })
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }
    }

    // en-US + common Outlook localizations (never click Delete).
    [GeneratedRegex(
        "Archive|Archivieren|Archiver|Archivia|Archivar|アーカイブ|归档|封存",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveButtonRegex();

    [GeneratedRegex(
        "Create new folder|New folder|Neuer Ordner|Nouveau dossier|Nuova cartella|Nueva carpeta|新建文件夹|新規フォルダー",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateFolderRegex();

    [GeneratedRegex("^(Move to|Move|Verschieben|Déplacer|Sposta|Mover|移动到|移動)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MoveButtonRegex();

    [GeneratedRegex(
        @"^(re|fw|fwd|aw|sv|antw|rif|回复|转发)\s*[:：]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPrefixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    public sealed class MailListItem
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("subject")]
        public string? Subject { get; set; }

        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("href")]
        public string? Href { get; set; }

        [JsonPropertyName("unread")]
        public bool? Unread { get; set; }

        [JsonPropertyName("kind")]
        public string? Kind { get; set; }
    }
}
