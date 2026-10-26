namespace Orbit.Agents.Browser;

/// <summary>
/// Stable on-disk folder ids under <c>data/browser-profiles/{agentId}/</c>.
/// Hash prefix <c>mb-</c> is historical (kept so existing profile folders still resolve).
/// </summary>
public static class BrowserProfileIds
{
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var trimmed = name.Trim().ToLowerInvariant();
        Span<char> buffer = stackalloc char[trimmed.Length];
        var n = 0;
        var prevDash = false;
        foreach (var ch in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                buffer[n++] = ch;
                prevDash = false;
            }
            else if (!prevDash)
            {
                buffer[n++] = '-';
                prevDash = true;
            }
        }

        var id = new string(buffer[..n]).Trim('-');
        if (id.Length > 0)
        {
            return id;
        }

        // Non-ASCII display names: stable short hash so two names don't collide on empty slug.
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(name.Trim())))[..8].ToLowerInvariant();
        return "mb-" + hash;
    }
}
