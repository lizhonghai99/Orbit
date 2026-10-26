using System.Security.Cryptography;
using System.Text;

namespace Orbit.Contracts;

/// <summary>
/// Shared auth decision for Runtime HTTP (and unit tests).
/// Token comparison is fixed-time; <c>GET /health</c> is exempt.
/// </summary>
public static class OrbitLocalAuthGate
{
    public static bool IsHealthExempt(string method, string path) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Agent token may only unlock <c>POST /sessions/{id}/agent-meta</c> (stage progress).
    /// </summary>
    public static bool IsAgentMetaPath(string method, string path) =>
        string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
        && path.StartsWith("/sessions/", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith("/agent-meta", StringComparison.OrdinalIgnoreCase);

    public static bool IsAuthorized(string? presentedToken, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken) || string.IsNullOrWhiteSpace(expectedToken))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(presentedToken.Trim());
        var b = Encoding.UTF8.GetBytes(expectedToken);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
