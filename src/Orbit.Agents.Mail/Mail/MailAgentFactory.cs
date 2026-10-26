using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orbit.Agents.Browser;
using Orbit.Agents.Hosting;

namespace Orbit.Agents.Mail.Mail;

public static class MailAgentFactory
{
    public static AIAgent Create(
        string agentId,
        string displayName,
        string instructions,
        BrowserSessionService browser,
        MailboxRegistry? mailboxRegistry = null,
        ILoggerFactory? loggerFactory = null)
    {
        var browserTools = new BrowserTools(browser);
        var mailTools = new MailTools(browser);
        var registry = mailboxRegistry ?? new MailboxRegistry(agentId, AppContext.BaseDirectory);
        registry.EnsureDefault(browser.ActiveProfileId);
        var mailboxTools = new MailboxTools(browser, registry);

        // Mutations: ApprovalRequiredAIFunction → AG-UI interrupt. No Runtime /actions/apply.
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(browserTools.OpenLoginPageAsync, name: "open_login_page",
                description: "Pop a visible Chromium window for Outlook login (only when not logged in)."),
            AIFunctionFactory.Create(browserTools.GetBrowserStatusAsync, name: "get_browser_status",
                description: "Browser URL/title/status + active profile id."),
            AIFunctionFactory.Create(browserTools.WaitForUrlChangeAsync, name: "wait_for_url_change",
                description: "Wait until URL changes after login."),
            AIFunctionFactory.Create(mailboxTools.ListMailboxes, name: "list_mailboxes",
                description: "List named mailboxes / Chromium profiles and setup status."),
            AIFunctionFactory.Create(mailboxTools.RegisterMailboxAsync, name: "register_mailbox",
                description: "Register another mailbox with a short name and create its browser profile."),
            AIFunctionFactory.Create(mailboxTools.SwitchMailboxAsync, name: "switch_mailbox",
                description: "Switch Chromium to another registered mailbox profile."),
            AIFunctionFactory.Create(mailboxTools.MarkMailboxesSetupDone, name: "mark_mailboxes_setup_done",
                description: "Mark that the user was asked about additional mailboxes."),
            AIFunctionFactory.Create(mailTools.GoToInboxAsync, name: "go_to_inbox",
                description: "Navigate to Outlook inbox."),
            AIFunctionFactory.Create(mailTools.ListInboxMailsAsync, name: "list_inbox_mails",
                description: "List visible inbox messages."),
            AIFunctionFactory.Create(mailTools.OrganizeInboxAsync, name: "organize_inbox",
                description: "Read inbox and suggest a plan in markdown; execute mutations via create_folder/move_mail/archive_mail."),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                mailTools.CreateFolderAsync, name: "create_folder",
                description: "Create a mailbox folder (requires user approval).")),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                mailTools.MoveMailAsync, name: "move_mail",
                description: "Move one mail to a folder (requires user approval).")),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                mailTools.ArchiveMailAsync, name: "archive_mail",
                description: "Archive one mail; never delete (requires user approval)."))
        };

        return AgentBootstrap.CreateAgent(
            agentId,
            displayName,
            instructions + """

                Tool policy:
                - Prefer tools over guessing inbox contents.
                - organize_inbox / list_inbox_mails: read + plan in Markdown; then mutate with create_folder / move_mail / archive_mail one at a time.
                - Mutations require tool approval — call the tools; do not only ask in plain text.
                - Only one approval interrupt is active; ask the user to act on the current card.
                - Never delete; never claim archive/move/folder succeeded without approval.
                - User-facing replies: Markdown. Prefer tool `markdown` tables. Link mail with [Open](href) when href exists.
                - Do not dump raw JSON. Use load_skill for playbooks; do not invent skill body text.

                Multi-mailbox:
                - Call list_mailboxes early. Profiles are isolated (cookies per id under browser-profiles).
                - After the first successful login (not on login wall), if askedAboutMore is false: ask the user in their language whether they have another mailbox.
                  - If no → mark_mailboxes_setup_done. The current one stays default.
                  - If yes → ask for a short name, then register_mailbox(name), then open_login_page for that profile.
                - When the user says "login to work/another mailbox": list_mailboxes → switch_mailbox → open_login_page if needed.
                - Keep multi-step organize progress in todos_*; lasting folder plans / triage rules in file_memory_*.
                """,
            tools,
            contentRoot: AppContext.BaseDirectory,
            loggerFactory: loggerFactory);
    }
}
