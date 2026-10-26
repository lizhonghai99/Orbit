using Orbit.Contracts;

namespace Orbit.Shell.Services;

/// <summary>
/// Shell-side LLM UI helpers. Truth source is Runtime (<c>/llm</c>); this type only
/// caches the last status for sync UI reads and exposes shared presets.
/// </summary>
public static class LlmEnvStore
{
    public const string ActiveProfileKey = LlmProfileCatalog.ActiveProfileKey;
    public const string ActiveModelKey = LlmProfileCatalog.ActiveModelKey;
    public const string ActiveProtocolKey = LlmProfileCatalog.ActiveProtocolKey;

    public static readonly string[] ReasoningEffortChoices = LlmProfileCatalog.ReasoningEffortChoices;

    public static LlmProfileCatalog.LlmPreset[] Presets => LlmProfileCatalog.Presets;

    private static readonly object Gate = new();
    private static LlmStatusDto? _cached;
    private static bool _reasoningEnabled = true;
    private static string _reasoningEffort = "medium";

    public static void CacheStatus(LlmStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (Gate)
        {
            _cached = status;
            _reasoningEnabled = status.ReasoningEnabled;
            if (!string.IsNullOrWhiteSpace(status.ReasoningEffort))
            {
                _reasoningEffort = status.ReasoningEffort.Trim().ToLowerInvariant();
            }
        }
    }

    public static void CacheReasoning(bool enabled, string effort)
    {
        lock (Gate)
        {
            _reasoningEnabled = enabled;
            _reasoningEffort = string.IsNullOrWhiteSpace(effort) ? "medium" : effort.Trim().ToLowerInvariant();
        }
    }

    public static LlmStatusDto GetStatus()
    {
        lock (Gate)
        {
            return _cached ?? new LlmStatusDto
            {
                Mode = "missing_key",
                HasApiKey = false,
                Profiles = Presets.Select(p => new LlmProfileInfo
                {
                    Id = p.Id,
                    DisplayName = p.DisplayName,
                    Protocol = LlmProtocol.Responses,
                    Endpoint = p.DefaultEndpoint,
                    HasApiKey = false,
                    Model = p.DefaultModel,
                    IsActive = false
                }).ToList()
            };
        }
    }

    public static bool IsReasoningEnabled()
    {
        lock (Gate)
        {
            return _reasoningEnabled;
        }
    }

    public static string ResolveReasoningEffort()
    {
        lock (Gate)
        {
            return ReasoningEffortChoices.Contains(_reasoningEffort, StringComparer.OrdinalIgnoreCase)
                ? _reasoningEffort
                : "medium";
        }
    }

    public static string? ResolveApiKey(string? profileId = null) => null;

    public static string? ResolveEndpoint(string? profileId = null)
    {
        var status = GetStatus();
        profileId ??= status.ActiveProfileId;
        var profile = status.Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        return profile?.Endpoint ?? FindPreset(profileId)?.DefaultEndpoint;
    }

    public static string? ResolveModel() => GetStatus().Model;

    public static string? NormalizeEndpointForProtocol(string? endpoint, LlmProtocol protocol) =>
        LlmProfileCatalog.NormalizeEndpointForProtocol(endpoint, protocol);

    public static LlmProfileCatalog.LlmPreset? FindPreset(string? id) => LlmProfileCatalog.Find(id);

    /// <summary>No-op: Runtime bootstraps from its own store / migrated User env.</summary>
    public static void BootstrapActiveProfileIfNeeded()
    {
    }
}
