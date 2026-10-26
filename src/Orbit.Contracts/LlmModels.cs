namespace Orbit.Contracts;

/// <summary>LLM wire protocol. Phase 1: Responses only.</summary>
public enum LlmProtocol
{
    Responses = 0,
    Messages = 1
}

public sealed class LlmProfileInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required LlmProtocol Protocol { get; init; }
    public string? Endpoint { get; init; }
    /// <summary>True when an API key is present in environment (value never returned).</summary>
    public bool HasApiKey { get; init; }
    public string? Model { get; init; }
    public bool IsActive { get; init; }
}

public sealed class LlmStatusDto
{
    public required string Mode { get; init; }
    public string? ActiveProfileId { get; init; }
    public string? Protocol { get; init; }
    public string? Model { get; init; }
    public string? EndpointHost { get; init; }
    public bool HasApiKey { get; init; }
    public bool ReasoningEnabled { get; init; } = true;
    public string? ReasoningEffort { get; init; }
    public IReadOnlyList<LlmProfileInfo> Profiles { get; init; } = [];
}

public sealed class SaveLlmProfileRequest
{
    public required string ProfileId { get; init; }
    public required string Endpoint { get; init; }
    /// <summary>Omit or empty to keep existing key.</summary>
    public string? ApiKey { get; init; }
    public LlmProtocol Protocol { get; init; } = LlmProtocol.Responses;
    public string? Model { get; init; }
    public bool SetActive { get; init; } = true;
}

public sealed class ListLlmModelsRequest
{
    /// <summary>Optional. When omitted, uses the saved profile endpoint. A different host requires <see cref="ApiKey"/>.</summary>
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? ProfileId { get; init; }
}

public sealed class LlmModelsResponse
{
    public IReadOnlyList<string> Models { get; init; } = [];
    public string? Error { get; init; }
}

public sealed class SetLlmModelRequest
{
    public required string Model { get; init; }
}

public sealed class SetLlmReasoningRequest
{
    public bool Enabled { get; init; }
    public string? Effort { get; init; }
}

public sealed class ReloadRunningAgentsResult
{
    public IReadOnlyList<string> Restarted { get; init; } = [];
    public IReadOnlyDictionary<string, string> Failures { get; init; } =
        new Dictionary<string, string>();
}
