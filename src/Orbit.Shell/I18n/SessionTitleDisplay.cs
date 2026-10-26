namespace Orbit.Shell.I18n;

/// <summary>
/// Session titles are stored as plain text. Default "new chat" titles were historically
/// written in the UI locale at create time — map those back to <c>chat.new</c> on display.
/// </summary>
public static class SessionTitleDisplay
{
    private static readonly HashSet<string> DefaultTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "New chat",
        "New Chat",
        "新对话",
    };

    public static bool IsDefault(string? title) =>
        string.IsNullOrWhiteSpace(title) || DefaultTitles.Contains(title.Trim());

    public static string Display(string? title) =>
        IsDefault(title) ? Loc.T("chat.new") : title!.Trim();
}
