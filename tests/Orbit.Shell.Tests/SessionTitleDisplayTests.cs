using Orbit.Shell.I18n;
using Xunit;

namespace Orbit.Shell.Tests;

public class SessionTitleDisplayTests
{
    public SessionTitleDisplayTests()
    {
        Loc.Initialize();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("New chat")]
    [InlineData("新对话")]
    public void Default_titles_map_to_chat_new(string? stored)
    {
        Assert.True(SessionTitleDisplay.IsDefault(stored));
        Assert.Equal(Loc.T("chat.new"), SessionTitleDisplay.Display(stored));
    }

    [Fact]
    public void Custom_title_is_kept()
    {
        Assert.False(SessionTitleDisplay.IsDefault("Inbox triage"));
        Assert.Equal("Inbox triage", SessionTitleDisplay.Display("Inbox triage"));
    }
}
