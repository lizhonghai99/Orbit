using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

public class OrbitAskToolsTests
{
    [Fact]
    public void ApplySelected_non_ask_tool_ignores_selected()
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["path"] = "a.txt"
        };

        var result = OrbitAskTools.ApplySelectedOnApprove(
            "file_access_write",
            args,
            selected: "sneaky",
            approved: true);

        Assert.NotNull(result);
        Assert.False(result!.ContainsKey("selected"));
        Assert.Equal("a.txt", result["path"]);
    }

    [Fact]
    public void ApplySelected_choice_without_selection_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            OrbitAskTools.ApplySelectedOnApprove(
                OrbitAskTools.ChoiceName,
                new Dictionary<string, object?>(),
                selected: null,
                approved: true));

        Assert.StartsWith("selection_required", ex.Message);
    }

    [Fact]
    public void ApplySelected_confirm_overwrites_model_prefill()
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["selected"] = "rejected",
            ["prompt"] = "Ship it?"
        };

        var result = OrbitAskTools.ApplySelectedOnApprove(
            OrbitAskTools.ConfirmName,
            args,
            selected: null,
            approved: true);

        Assert.Equal("approved", result!["selected"]);
    }

    [Fact]
    public void ApplySelected_confirm_uses_caller_selected()
    {
        var result = OrbitAskTools.ApplySelectedOnApprove(
            OrbitAskTools.ConfirmName,
            new Dictionary<string, object?> { ["selected"] = "rejected" },
            selected: "approved",
            approved: true);

        Assert.Equal("approved", result!["selected"]);
    }

    [Fact]
    public void TryNormalizeChoice_rejects_too_many_options()
    {
        var options = Enumerable.Range(1, 9).Select(i => $"opt{i}").ToArray();
        Assert.False(OrbitAskTools.TryNormalizeChoice(
            "pick one",
            options,
            out _,
            out _,
            out var error));
        Assert.Contains("between", error);
    }

    [Fact]
    public void TryNormalizeChoice_accepts_valid()
    {
        Assert.True(OrbitAskTools.TryNormalizeChoice(
            "pick",
            ["A", "B"],
            out var prompt,
            out var options,
            out var error));
        Assert.Null(error);
        Assert.Equal("pick", prompt);
        Assert.Equal(["A", "B"], options);
    }

    [Fact]
    public void Parse_harness_collects_ignored_tokens()
    {
        var parsed = OrbitHarnessIds.Parse(["mode", "file_acess", "todo"]);
        Assert.Contains("mode", parsed.Enabled);
        Assert.Contains("todo", parsed.Enabled);
        Assert.DoesNotContain("file_access", parsed.Enabled);
        Assert.Contains("file_acess", parsed.Ignored);
    }
}
