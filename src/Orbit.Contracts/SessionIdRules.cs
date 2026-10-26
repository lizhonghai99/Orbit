using System.Text.RegularExpressions;

namespace Orbit.Contracts;

/// <summary>
/// Session ids are always <c>Guid.NewGuid("N")</c> (32 hex). Reject path segments before any disk join.
/// </summary>
public static partial class SessionIdRules
{
    public static bool IsValid(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && IdRegex().IsMatch(sessionId);

    public static string RequireValid(string? sessionId)
    {
        if (!IsValid(sessionId))
        {
            throw new ArgumentException("Invalid session id.", nameof(sessionId));
        }

        return sessionId!;
    }

    [GeneratedRegex("^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();
}
