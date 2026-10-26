using System.Security.Cryptography;
using System.Text;

namespace Orbit.Runtime.Llm;

/// <summary>
/// DPAPI (CurrentUser) protect/unprotect for LLM API keys on disk.
/// In-memory / process-env values stay plaintext for Agent children.
/// </summary>
internal static class LlmApiKeyProtector
{
    private const string Prefix = "dpapi:v1:";

    public static string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        EnsureWindows();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
#pragma warning disable CA1416 // Orbit Runtime is Windows-local; DPAPI CurrentUser is intentional.
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// Returns plaintext. Legacy (no prefix) values pass through unchanged so callers can re-persist.
    /// </summary>
    public static string Unprotect(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        EnsureWindows();
        var b64 = stored[Prefix.Length..];
        var protectedBytes = Convert.FromBase64String(b64);
#pragma warning disable CA1416
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        return Encoding.UTF8.GetString(bytes);
    }

    public static bool IsProtected(string? stored) =>
        !string.IsNullOrWhiteSpace(stored)
        && stored.StartsWith(Prefix, StringComparison.Ordinal);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "LLM API key DPAPI protection requires Windows (CurrentUser scope).");
        }
    }
}
