using System.ComponentModel;
using Microsoft.Extensions.AI;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Shared HITL tools injected by <see cref="AgentBootstrap"/> for every agent.
/// Wrapped in <see cref="ApprovalRequiredAIFunction"/> so Shell can render choice/confirm cards;
/// the selected value is injected into <c>selected</c> on approve before the function runs
/// (see <see cref="OrbitAskTools.ApplySelectedOnApprove"/>).
/// </summary>
public static class AskUserTools
{
    public const string ChoiceName = OrbitAskTools.ChoiceName;
    public const string ConfirmName = OrbitAskTools.ConfirmName;

    public static bool IsAskUserTool(string? toolName) => OrbitAskTools.IsAskUserTool(toolName);

    public static IReadOnlyList<AITool> CreateApprovalRequired() =>
    [
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(AskChoice, name: ChoiceName)),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(AskConfirm, name: ConfirmName)),
    ];

    [Description(
        "Ask the user to pick exactly one option (clarification, direction, A/B). "
        + "Prefer this over asking them to type A/B in chat. Returns the selected option label. "
        + "Limits: 2–8 options, each ≤120 chars; prompt ≤2000 chars.")]
    private static string AskChoice(
        [Description("Short question shown on the card.")] string prompt,
        [Description("Two to eight options the user can click.")] string[] options,
        [Description("Filled by Shell after the user clicks; do not set this yourself.")]
        string? selected = null)
    {
        if (!OrbitAskTools.TryNormalizeChoice(prompt, options, out _, out _, out var error))
        {
            return error ?? "ask_user_choice rejected: invalid options.";
        }

        if (string.IsNullOrWhiteSpace(selected))
        {
            return "(no selection)";
        }

        return selected.Trim();
    }

    [Description(
        "Present a plan or confirmation card (Approve / Reject). "
        + "Prefer this over asking the user to type approval words in chat. Returns 'approved' or is rejected via HITL.")]
    private static string AskConfirm(
        [Description("Plan summary or confirmation question (Markdown ok).")] string prompt,
        [Description("Optional title for the card (display only; ignored by the tool body).")]
        string? title = null,
        [Description("Filled by Shell on Approve; do not set this yourself.")]
        string? selected = null)
    {
        // title is for Shell card display via tool args; Runtime injects selected on approve.
        _ = title;
        _ = prompt;
        return string.IsNullOrWhiteSpace(selected) ? "approved" : selected.Trim();
    }
}
