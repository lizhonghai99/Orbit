using Orbit.Agents.Mail.Mail;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public class MailSubjectNormalizeTests
{
    [Theory]
    [InlineData("Re: Hello", "Hello")]
    [InlineData("FW: Hello", "Hello")]
    [InlineData("回复: 你好", "你好")]
    [InlineData("  Re:   Fw:  Hello  world  ", "Hello world")]
    public void NormalizeSubject_strips_prefixes_and_whitespace(string raw, string expected) =>
        Assert.Equal(expected, MailInboxActions.NormalizeSubject(raw), StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("Invoice", "Invoice", true)]
    [InlineData("Invoice", "Invoice — Q3", true)]
    [InlineData("Invoice", "Your Invoice …", false)]
    [InlineData("Re: Invoice", "Invoice", true)]
    public void SubjectsMatchForArchive_equal_or_got_starts_with_want(
        string expected,
        string actual,
        bool match) =>
        Assert.Equal(match, MailInboxActions.SubjectsMatchForArchive(expected, actual));
}
