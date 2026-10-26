using System.ComponentModel;
using System.Text.Json;
using Orbit.Agents.Browser;

namespace Orbit.Agents.Mail.Mail;

/// <summary>Named Outlook mailboxes: FileMemory registry + Chromium profile switch via Browser.</summary>
public sealed class MailboxTools(BrowserSessionService browser, MailboxRegistry registry)
{
    [Description(
        "List registered mailboxes / browser profiles, which one is active, and whether the user was already asked about additional accounts.")]
    public string ListMailboxes()
    {
        var state = registry.EnsureDefault(browser.ActiveProfileId);
        // Keep registry active id aligned with the live browser profile.
        if (!string.Equals(state.ActiveId, browser.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                state = registry.SetActive(browser.ActiveProfileId);
            }
            catch (KeyNotFoundException)
            {
                state = registry.EnsureDefault(browser.ActiveProfileId);
            }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            action = "list_mailboxes",
            activeId = browser.ActiveProfileId,
            askedAboutMore = state.AskedAboutMore,
            memoryRoot = registry.MemoryRoot,
            profilesRoot = browser.ProfilesRoot,
            mailboxes = state.Mailboxes.Select(m => new
            {
                m.Id,
                m.Name,
                m.IsDefault,
                active = string.Equals(m.Id, browser.ActiveProfileId, StringComparison.OrdinalIgnoreCase),
                profileDir = browser.EnsureProfileDirectory(m.Id),
                m.CreatedAt
            }),
            hint = state.AskedAboutMore
                ? "Registry ready. Use switch_mailbox to change accounts; register_mailbox to add one."
                : "After first successful login, ask whether the user has another mailbox. If yes, register_mailbox with a short name; if no, call mark_mailboxes_setup_done."
        });
    }

    [Description(
        "Register a named mailbox (creates a Chromium profile folder). Use after the user says they have another account and picks a name like work/personal.")]
    public async Task<string> RegisterMailboxAsync(
        [Description("Display name, e.g. work / personal / company")] string name,
        [Description("Switch browser to this mailbox immediately after create")] bool switchNow = true,
        [Description("Mark as the default mailbox")] bool makeDefault = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var state = registry.Register(name, setActive: false, isDefault: makeDefault);
            var record = registry.Find(state, name)
                         ?? state.Mailboxes.Last();
            var profileDir = browser.EnsureProfileDirectory(record.Id);

            if (switchNow)
            {
                await browser.SwitchProfileAsync(record.Id, cancellationToken).ConfigureAwait(false);
                state = registry.SetActive(record.Id);
            }

            return JsonSerializer.Serialize(new
            {
                ok = true,
                action = "register_mailbox",
                mailbox = new { record.Id, record.Name, record.IsDefault },
                activeId = browser.ActiveProfileId,
                profileDir,
                askedAboutMore = state.AskedAboutMore,
                hint = switchNow
                    ? $"Created and switched to '{record.Name}'. Call open_login_page to sign in."
                    : $"Registered '{record.Name}'. Use switch_mailbox when ready, then sign in."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    [Description(
        "Switch the browser to another registered mailbox profile (cookies/login are per profile). Then open_login_page if that account is not logged in yet.")]
    public async Task<string> SwitchMailboxAsync(
        [Description("Mailbox id or display name")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var state = registry.EnsureDefault();
            var match = registry.Find(state, idOrName);
            if (match is null)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = $"Unknown mailbox '{idOrName}'. Call list_mailboxes or register_mailbox first.",
                    mailboxes = state.Mailboxes.Select(m => m.Id)
                });
            }

            var profileDir = await browser.SwitchProfileAsync(match.Id, cancellationToken).ConfigureAwait(false);
            state = registry.SetActive(match.Id);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                action = "switch_mailbox",
                mailbox = new { match.Id, match.Name, match.IsDefault },
                activeId = browser.ActiveProfileId,
                profileDir,
                hint = "Switched Chromium profile. If not signed in, call open_login_page; write inbox inventory to inbox-{id}-status.md."
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    [Description(
        "Record that the user was asked whether they have more mailboxes (call when they say no, or after registering additional ones).")]
    public string MarkMailboxesSetupDone(
        [Description("True when the prompt was completed")] bool done = true)
    {
        var state = registry.MarkAskedAboutMore(done);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            action = "mark_mailboxes_setup_done",
            askedAboutMore = state.AskedAboutMore,
            activeId = state.ActiveId,
            count = state.Mailboxes.Count,
            hint = done
                ? "Recorded: stop prompting about extra mailboxes (user can still register_mailbox anytime)."
                : "Cleared flag: may ask again after the next successful login."
        });
    }
}
