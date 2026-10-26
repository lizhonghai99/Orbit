using Microsoft.Extensions.Logging;
using Orbit.Agents.Browser;
using Orbit.Agents.Hosting;
using Orbit.Agents.Mail.Mail;

var agentId = Environment.GetEnvironmentVariable("ORBIT_AGENT_ID") ?? "mail";
var loginUrl = Environment.GetEnvironmentVariable("ORBIT_LOGIN_URL");
if (string.IsNullOrWhiteSpace(loginUrl))
{
    loginUrl = "https://outlook.live.com/mail/";
}

var app = AgentHost.BuildAguiHost(
    args,
    agentId,
    configureServices: builder =>
    {
        var browser = builder.Services.AddOrbitBrowser(agentId, loginUrl, MailBrowserPolicy.SessionOptions);
        var mailboxes = new MailboxRegistry(agentId, Orbit.Contracts.OrbitPathNames.TryGetOrbitRoot());
        mailboxes.EnsureDefault(browser.ActiveProfileId);
        builder.Services.AddSingleton(mailboxes);
        builder.Services.AddSingleton(sp => MailAgentFactory.Create(
            agentId,
            "Mail Assistant",
            """
            You are Orbit's Mail Assistant for Outlook on the web.

            Goal: help the user understand and organize inbox mail with tools — not guesswork.
            Typical flow: list_mailboxes → get_browser_status → open_login_page (if needed) →
            go_to_inbox → list_inbox_mails / organize_inbox → mutate only via create_folder / move_mail / archive_mail.
            Multi-account: after first login ask about other mailboxes; switch_mailbox when the user names another account.

            Planning: prefer plan mode for multi-step organize; switch to execute after the user agrees.
            Use todos_* for multi-step checklists; file_memory_* for notes that must survive compaction (folder plans, triage rules).

            Safety: never delete mail; never claim a mutation succeeded without tool approval.
            Reply in the user's language when they write Chinese or English.
            """,
            browser,
            mailboxes,
            loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
    },
    healthExtras: sp =>
    {
        var browser = sp.GetRequiredService<BrowserSessionService>();
        return new Dictionary<string, string>
        {
            ["capability"] = "browser+mail",
            ["loginUrl"] = browser.DefaultLoginUrl,
            ["mailboxId"] = browser.ActiveProfileId,
            ["profileDir"] = browser.ProfileDir,
            ["headless"] = browser.Headless ? "true" : "false"
        };
    });

app.Lifetime.ApplicationStopped.Register(() =>
{
    var browser = app.Services.GetRequiredService<BrowserSessionService>();
    browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

try
{
    await app.RunAsync().ConfigureAwait(false);
}
finally
{
    await Serilog.Log.CloseAndFlushAsync().ConfigureAwait(false);
}
