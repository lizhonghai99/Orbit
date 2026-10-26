using System.Text;

namespace Orbit.Contracts;

/// <summary>HTTP header names for per-run workspace (Runtime → Agent).</summary>
public static class OrbitWorkspaceHeaders
{
    public const string Workspace = "X-Orbit-Workspace";
    public const string WorkspaceId = "X-Orbit-Workspace-Id";
    public const string RunId = "X-Orbit-Run-Id";
    public const string SessionId = "X-Orbit-Session-Id";

    private const string Base64Prefix = "b64:";

    /// <summary>UTF-8 path as <c>b64:...</c> so non-ASCII paths survive Latin-1 HTTP headers.</summary>
    public static string EncodePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Base64Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(path.Trim()));
    }

    /// <summary>Decode <see cref="EncodePath"/> or accept legacy plaintext paths.</summary>
    public static string? DecodePath(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var raw = headerValue.Trim();
        if (raw.StartsWith(Base64Prefix, StringComparison.Ordinal))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(raw[Base64Prefix.Length..]));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return raw;
    }

    /// <summary>Apply session snapshot headers when path/id are present.</summary>
    public static void ApplyTo(System.Net.Http.Headers.HttpRequestHeaders headers, string? workspacePath, string? workspaceId)
    {
        headers.Remove(Workspace);
        headers.Remove(WorkspaceId);
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            headers.TryAddWithoutValidation(Workspace, EncodePath(workspacePath));
        }

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            headers.TryAddWithoutValidation(WorkspaceId, workspaceId.Trim());
        }
    }

    public static void ApplyTo(System.Net.Http.Headers.HttpRequestHeaders headers, SessionHeader session) =>
        ApplyTo(headers, session.WorkspacePath, session.WorkspaceId);
}
