using Orbit.Agents.Browser;
using Orbit.Agents.Mail.Mail;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class BrowserNavigationAllowlistTests
{
    [Fact]
    public void Mail_policy_blocks_model_supplied_file_and_metadata_urls()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-nav-" + Guid.NewGuid().ToString("N"));
        try
        {
            var browser = new BrowserSessionService(
                "mail",
                "https://outlook.live.com/mail/",
                root,
                preferHeadless: true,
                MailBrowserPolicy.SessionOptions);

            Assert.True(browser.IsNavigationAllowed("https://outlook.live.com/mail/"));
            Assert.False(browser.IsNavigationAllowed("file:///C:/Users/.ssh/id_rsa"));
            Assert.False(browser.IsNavigationAllowed("http://169.254.169.254/latest/meta-data/"));
            Assert.False(browser.IsNavigationAllowed("https://evil.example/login"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void Empty_allowlist_is_fail_closed()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-nav-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            var browser = new BrowserSessionService(
                "x",
                "https://outlook.live.com/mail/",
                root,
                preferHeadless: true,
                options: new BrowserSessionOptions());

            Assert.False(browser.IsNavigationAllowed("https://outlook.live.com/mail/"));
            Assert.NotEmpty(MailBrowserPolicy.AllowedHosts);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
