namespace Orbit.Contracts;

/// <summary>Shared LLM provider presets (Shell UI + Runtime settings).</summary>
public static class LlmProfileCatalog
{
    public const string ActiveProfileKey = "ORBIT_LLM_PROFILE";
    public const string ActiveModelKey = "ORBIT_LLM_MODEL";
    public const string ActiveProtocolKey = "ORBIT_LLM_PROTOCOL";
    public const string ReasoningEnabledKey = "ORBIT_REASONING_ENABLED";
    public const string ReasoningEffortKey = "ORBIT_REASONING_EFFORT";

    public static readonly string[] ReasoningEffortChoices = ["low", "medium", "high", "extrahigh"];

    public static readonly LlmPreset[] Presets =
    [
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "DEEPSEEK_API_KEY", "DEEPSEEK_END_POINT", "deepseek-flash"),
        new("moonshot", "Moonshot / Kimi", "https://api.moonshot.cn/v1", "MOONSHOT_API_KEY", "MOONSHOT_END_POINT", "kimi-k2.6"),
        new("custom", "Custom (OpenAI Responses)", "", "ORBIT_LLM_API_KEY", "ORBIT_LLM_ENDPOINT", "")
    ];

    public static LlmPreset? Find(string? id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string? NormalizeEndpointForProtocol(string? endpoint, LlmProtocol protocol)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        var trimmed = endpoint.Trim().TrimEnd('/');
        if (protocol != LlmProtocol.Responses)
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/anthropic".Length].TrimEnd('/');
            if (trimmed.Contains("moonshot", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("kimi", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed + "/v1";
            }
        }

        return trimmed;
    }

    public sealed record LlmPreset(
        string Id,
        string DisplayName,
        string DefaultEndpoint,
        string ApiKeyEnv,
        string EndpointEnv,
        string DefaultModel);
}
