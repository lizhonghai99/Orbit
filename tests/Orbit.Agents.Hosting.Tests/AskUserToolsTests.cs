using Orbit.Agents.Hosting;
using Xunit;

namespace Orbit.Agents.Hosting.Tests;

public sealed class AskUserToolsTests
{
    [Fact]
    public void CreateApprovalRequired_registers_choice_and_confirm()
    {
        var tools = AskUserTools.CreateApprovalRequired();
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == AskUserTools.ChoiceName);
        Assert.Contains(tools, t => t.Name == AskUserTools.ConfirmName);
    }

    [Theory]
    [InlineData("ask_user_choice", true)]
    [InlineData("ask_user_confirm", true)]
    [InlineData("tavily_search", false)]
    public void IsAskUserTool_detects_names(string name, bool expected) =>
        Assert.Equal(expected, AskUserTools.IsAskUserTool(name));
}
