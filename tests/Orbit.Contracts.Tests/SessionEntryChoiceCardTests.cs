using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public sealed class SessionEntryChoiceCardTests
{
    [Fact]
    public void TryNormalizeChoice_accepts_valid_options()
    {
        Assert.True(OrbitAskTools.TryNormalizeChoice(
            "Which option?",
            ["A · first", "B · second"],
            out var prompt,
            out var options,
            out var error));
        Assert.Null(error);
        Assert.Equal("Which option?", prompt);
        Assert.Equal(2, options.Count);
        Assert.Equal("A · first", options[0]);
    }

    [Fact]
    public void TryNormalizeChoice_rejects_empty_options()
    {
        Assert.False(OrbitAskTools.TryNormalizeChoice(
            "Pick one",
            Array.Empty<string>(),
            out _,
            out var options,
            out var error));
        Assert.NotNull(error);
        Assert.Empty(options);
    }

    [Fact]
    public void TryNormalizeChoice_rejects_blank_prompt()
    {
        Assert.False(OrbitAskTools.TryNormalizeChoice(
            "  ",
            ["one", "two"],
            out _,
            out _,
            out var error));
        Assert.Contains("prompt", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryNormalizeChoice_rejects_too_many_options()
    {
        var many = Enumerable.Range(1, OrbitAskTools.MaxOptions + 1).Select(i => $"opt-{i}").ToArray();
        Assert.False(OrbitAskTools.TryNormalizeChoice("Pick", many, out _, out _, out var error));
        Assert.NotNull(error);
    }
}
