using System.ComponentModel;
using Orbit.Agents.Browser;

namespace Orbit.Agents.Mail.Mail;

public sealed class MailTools(BrowserSessionService browser)
{
    [Description("Navigate to the Outlook inbox list page (headless; pops a window only if a login wall is detected).")]
    public async Task<string> GoToInboxAsync(CancellationToken cancellationToken = default)
    {
        var json = await browser.WithPageAsync(
            (page, ct) => MailInboxActions.GoToInboxAsync(page, browser.DefaultLoginUrl, ct),
            ensureStarted: true,
            cancellationToken).ConfigureAwait(false);

        if (await browser.RevealIfLoginWallAsync(cancellationToken).ConfigureAwait(false))
        {
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                action = "go_to_inbox",
                loginWall = true,
                headless = browser.IsEffectivelyHeadless,
                hint = "Login wall detected; visible browser opened — sign in, then organize."
            });
        }

        return json;
    }

    [Description("List messages currently visible in the inbox (read-only extraction).")]
    public async Task<string> ListInboxMailsAsync(
        [Description("Max messages to return")] int maxItems = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (await browser.RevealIfLoginWallAsync(cancellationToken).ConfigureAwait(false))
            {
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = false,
                    loginWall = true,
                    hint = "On login page; visible browser opened — sign in first."
                });
            }

            return await browser.WithPageAsync(async (page, ct) =>
            {
                var items = await MailInboxActions.ExtractInboxAsync(page, maxItems, ct).ConfigureAwait(false);
                var markdown = MailInboxActions.BuildInboxMarkdown(items);
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true,
                    action = "list_inbox_mails",
                    provider = MailInboxActions.LooksLikeOutlook(page.Url) ? "outlook" : "generic",
                    url = page.Url,
                    title = await page.TitleAsync().ConfigureAwait(false),
                    count = items.Count,
                    markdown,
                    hint = "Prefer echoing the markdown table to the user; [Open](url) links are clickable.",
                    items
                });
            }, ensureStarted: false, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = false,
                started = false,
                hint = "Browser not started. Call go_to_inbox first; a window opens automatically if login is required."
            });
        }
    }

    [Description("Propose keep/archive/move/review for inbox messages. Folder create/move/archive only after user approval; never deletes.")]
    public async Task<string> OrganizeInboxAsync(
        [Description("Max items to propose")] int maxItems = 20,
        CancellationToken cancellationToken = default)
    {
        maxItems = Math.Clamp(maxItems, 5, 40);
        try
        {
            if (await browser.RevealIfLoginWallAsync(cancellationToken).ConfigureAwait(false))
            {
                return MailInboxActions.BuildOrganizeJson(
                    [],
                    null,
                    null,
                    usedDemo: false,
                    "Login wall detected; browser opened — sign in, then organize.");
            }

            return await browser.WithPageAsync(async (page, ct) =>
            {
                var items = await MailInboxActions.ExtractInboxAsync(page, maxItems, ct).ConfigureAwait(false);
                var usable = items.Count(i => !string.IsNullOrWhiteSpace(i.Text)
                                             || !string.IsNullOrWhiteSpace(i.Subject)
                                             || !string.IsNullOrWhiteSpace(i.From));
                if (usable == 0)
                {
                    return MailInboxActions.BuildOrganizeJson(
                        [],
                        page.Url,
                        await page.TitleAsync().ConfigureAwait(false),
                        usedDemo: false,
                        "No readable mail titles. Confirm Outlook inbox is signed in and refreshed.");
                }

                return MailInboxActions.BuildOrganizeJson(
                    items,
                    page.Url,
                    await page.TitleAsync().ConfigureAwait(false),
                    usedDemo: false,
                    "Organize proposals ready; archive/create folder/move only after approval — never auto-delete.");
            }, ensureStarted: false, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return MailInboxActions.BuildOrganizeJson(
                [],
                null,
                null,
                usedDemo: false,
                "Browser not started. Call go_to_inbox / sign in, then organize.");
        }
    }

    [Description("Create a mailbox folder in Outlook UI. Requires user approval before execution.")]
    public Task<string> CreateFolderAsync(
        [Description("Folder name to create")] string folderName,
        CancellationToken cancellationToken = default) =>
        ApplyAsJsonAsync(new MailMutationRequest
        {
            SuggestedAction = "create_folder",
            TargetFolder = folderName?.Trim(),
            Text = $"Create folder '{folderName?.Trim()}'"
        }, cancellationToken);

    [Description("Move one mail into a folder. Requires user approval before execution.")]
    public Task<string> MoveMailAsync(
        [Description("Destination folder name")] string folderName,
        [Description("Mail list text / subject for matching")] string? text = null,
        [Description("Mail href if known")] string? href = null,
        CancellationToken cancellationToken = default) =>
        ApplyAsJsonAsync(new MailMutationRequest
        {
            SuggestedAction = "move_to_folder",
            TargetFolder = folderName?.Trim(),
            Text = text,
            Href = href
        }, cancellationToken);

    [Description("Archive one mail (never delete). Requires user approval before execution.")]
    public Task<string> ArchiveMailAsync(
        [Description("Mail list text / subject for matching")] string? text = null,
        [Description("Mail href if known")] string? href = null,
        CancellationToken cancellationToken = default) =>
        ApplyAsJsonAsync(new MailMutationRequest
        {
            SuggestedAction = "archive_or_delete",
            Text = text,
            Href = href
        }, cancellationToken);

    public Task<MailMutationResult> ApplyAsync(
        MailMutationRequest request,
        CancellationToken cancellationToken = default) =>
        browser.WithPageAsync(
            (page, ct) => MailInboxActions.ApplyMailDecisionAsync(page, request, ct),
            ensureStarted: false,
            cancellationToken);

    private async Task<string> ApplyAsJsonAsync(
        MailMutationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            return System.Text.Json.JsonSerializer.Serialize(result);
        }
        catch (InvalidOperationException)
        {
            return System.Text.Json.JsonSerializer.Serialize(new MailMutationResult
            {
                Ok = false,
                Executed = false,
                Mode = "browser_not_started",
                Message = "Browser not started. Call go_to_inbox / sign in first.",
                Error = "browser_not_started"
            });
        }
    }
}
