namespace Orbit.Contracts;

/// <summary>
/// Shared context-window / max-output resolution for Shell meter, Agent compaction, and Runtime history.
/// Order: env override → model-id heuristics → defaults.
/// </summary>
public static class OrbitLlmContextWindow
{
    public const int DefaultTokens = 64_000;
    public const int DefaultMaxOutputTokens = 8_192;

    /// <summary>
    /// Resolve window size. When <paramref name="known"/> is false, the returned value is still
    /// <see cref="DefaultTokens"/> (compaction needs a positive cap). Shell meter should treat
    /// unknown as display 0: <c>known ? tokens : 0</c>.
    /// </summary>
    /// <param name="useEnvModelFallback">
    /// When true (Agent Hosting / Runtime), empty <paramref name="modelId"/> falls back to
    /// <c>ORBIT_LLM_MODEL</c>. Shell meter passes false so an empty session model stays unknown.
    /// </param>
    public static int Resolve(string? modelId, out bool known, bool useEnvModelFallback = true)
    {
        var env = OrbitEnv.Get("ORBIT_LLM_CONTEXT_WINDOW");
        if (int.TryParse(env, out var configured) && configured > 0)
        {
            known = true;
            return configured;
        }

        var m = (modelId
                 ?? (useEnvModelFallback ? OrbitEnv.Get("ORBIT_LLM_MODEL") : null)
                 ?? "").ToLowerInvariant();
        if (m.Contains("256k", StringComparison.Ordinal))
        {
            known = true;
            return 256_000;
        }

        if (m.Contains("32k", StringComparison.Ordinal))
        {
            known = true;
            return 32_000;
        }

        if (m.Contains("64k", StringComparison.Ordinal) || m.Contains("deepseek", StringComparison.Ordinal))
        {
            known = true;
            return DefaultTokens;
        }

        if (m.Contains("128k", StringComparison.Ordinal)
            || m.Contains("gpt-4", StringComparison.Ordinal)
            || m.Contains("gpt-5", StringComparison.Ordinal))
        {
            known = true;
            return 128_000;
        }

        known = false;
        return DefaultTokens;
    }

    public static int Resolve(string? modelId, bool useEnvModelFallback = true) =>
        Resolve(modelId, out _, useEnvModelFallback);

    public static int ResolveMaxOutputTokens()
    {
        var env = OrbitEnv.Get("ORBIT_LLM_MAX_OUTPUT_TOKENS");
        if (int.TryParse(env, out var configured) && configured > 0)
        {
            return configured;
        }

        return DefaultMaxOutputTokens;
    }
}
