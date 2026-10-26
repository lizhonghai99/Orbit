namespace Orbit.Contracts;

/// <summary>
/// Shared ask_user_* vocabulary and approve-time <c>selected</c> injection rules
/// (Runtime resume, Shell cards, Hosting tools).
/// </summary>
public static class OrbitAskTools
{
    public const string ChoiceName = "ask_user_choice";
    public const string ConfirmName = "ask_user_confirm";

    public const int MinOptions = 2;
    public const int MaxOptions = 8;
    public const int MaxOptionLength = 120;
    public const int MaxPromptLength = 2000;

    public static bool IsAskUserTool(string? toolName) =>
        IsChoice(toolName) || IsConfirm(toolName);

    public static bool IsChoice(string? toolName) =>
        string.Equals(toolName, ChoiceName, StringComparison.OrdinalIgnoreCase);

    public static bool IsConfirm(string? toolName) =>
        string.Equals(toolName, ConfirmName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Apply caller <paramref name="selected"/> on approve. Non-ask tools never get a
    /// <c>selected</c> key. Choice requires a non-empty selection; confirm always overwrites
    /// with the caller value or <c>approved</c> (model prefill is discarded).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Message starts with <c>selection_required</c> when choice is approved without a selection.
    /// </exception>
    public static Dictionary<string, object?>? ApplySelectedOnApprove(
        string? toolName,
        IDictionary<string, object?>? args,
        string? selected,
        bool approved)
    {
        if (!approved || !IsAskUserTool(toolName))
        {
            return args as Dictionary<string, object?>
                   ?? (args is null
                       ? null
                       : new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase));
        }

        var dict = args as Dictionary<string, object?>
                   ?? new Dictionary<string, object?>(
                       args ?? new Dictionary<string, object?>(),
                       StringComparer.OrdinalIgnoreCase);

        if (IsChoice(toolName))
        {
            if (string.IsNullOrWhiteSpace(selected))
            {
                throw new InvalidOperationException(
                    "selection_required: ask_user_choice approve needs a selected option.");
            }

            dict["selected"] = selected.Trim();
            return dict;
        }

        // confirm: never keep model-prefilled selected
        dict["selected"] = string.IsNullOrWhiteSpace(selected) ? "approved" : selected.Trim();
        return dict;
    }

    /// <summary>
    /// Validate / clamp choice card inputs. Returns false with an error instead of showing a usable card.
    /// </summary>
    public static bool TryNormalizeChoice(
        string? prompt,
        IEnumerable<string>? options,
        out string normalizedPrompt,
        out IReadOnlyList<string> normalizedOptions,
        out string? error)
    {
        normalizedPrompt = "";
        normalizedOptions = [];
        error = null;

        if (string.IsNullOrWhiteSpace(prompt))
        {
            error = "ask_user_choice requires a non-empty prompt.";
            return false;
        }

        var p = prompt.Trim();
        if (p.Length > MaxPromptLength)
        {
            error = $"ask_user_choice prompt exceeds {MaxPromptLength} characters.";
            return false;
        }

        var list = new List<string>();
        if (options is not null)
        {
            foreach (var raw in options)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var opt = raw.Trim();
                if (opt.Length > MaxOptionLength)
                {
                    error = $"ask_user_choice option exceeds {MaxOptionLength} characters.";
                    return false;
                }

                list.Add(opt);
            }
        }

        if (list.Count < MinOptions || list.Count > MaxOptions)
        {
            error =
                $"ask_user_choice requires between {MinOptions} and {MaxOptions} options (got {list.Count}).";
            return false;
        }

        normalizedPrompt = p;
        normalizedOptions = list;
        return true;
    }
}
