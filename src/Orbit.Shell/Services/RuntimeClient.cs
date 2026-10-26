using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Contracts;

namespace Orbit.Shell.Services;

public sealed class RuntimeClient : IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:5050";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Short calls (health, lists, settings) must not hang forever on a dead Runtime.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly HttpClient _longHttp;
    private readonly OrbitTokenRefreshHandler _tokenHandler;

    public RuntimeClient(string? baseUrl = null)
    {
        _tokenHandler = new OrbitTokenRefreshHandler(
            OrbitLocalAuth.TryReadToken,
            ApplyTokenHeader,
            new HttpClientHandler());

        // A turn (or a resumed approval) can legitimately run longer than any fixed HTTP timeout:
        // the Shell streams progress by polling while this request is in flight, and Stop cancels it.
        // So long calls use a client without a timeout instead of failing at 5 minutes mid-turn.
        var address = new Uri(baseUrl ?? DefaultBaseUrl);
        _http = new HttpClient(_tokenHandler, disposeHandler: false)
        {
            BaseAddress = address,
            Timeout = ShortTimeout
        };
        _longHttp = new HttpClient(_tokenHandler, disposeHandler: true)
        {
            BaseAddress = address,
            Timeout = Timeout.InfiniteTimeSpan
        };
        AttachLocalToken();
    }

    /// <summary>Read %LOCALAPPDATA%/Orbit/runtime.token written by Runtime on start.</summary>
    public void AttachLocalToken() => AttachLocalToken(OrbitLocalAuth.TryReadToken());

    private void AttachLocalToken(string? token) => ApplyTokenHeader(token);

    private void ApplyTokenHeader(string? token)
    {
        foreach (var client in new[] { _http, _longHttp })
        {
            client.DefaultRequestHeaders.Remove(OrbitLocalAuth.HeaderName);
            if (!string.IsNullOrWhiteSpace(token))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, token);
            }
        }
    }

    public async Task<RuntimeHealth?> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            // Health probes must not inherit the 5-minute turn timeout.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(3));
            using var req = new HttpRequestMessage(HttpMethod.Get, "/health");
            using var response = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<RuntimeHealth>(JsonOptions, linked.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task<List<AgentInfo>?> GetAgentsAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<AgentInfo>>("/agents", JsonOptions, ct);

    public async Task<IReadOnlyList<AgentManifestLoadError>> GetManifestErrorsAsync(
        CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("/agents/manifest-errors", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<List<AgentManifestLoadError>>(body, JsonOptions) ?? [];
    }

    public async Task<AgentInfo?> StartAgentAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"/agents/{id}/start", null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<AgentInfo>(body, JsonOptions);
    }

    public async Task<AgentInfo?> StopAgentAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"/agents/{id}/stop", null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<AgentInfo>(body, JsonOptions);
    }

    public Task<List<SessionSummary>?> GetSessionsAsync(bool includeArchived = false, CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<SessionSummary>>(
            includeArchived ? "/sessions?includeArchived=true" : "/sessions",
            JsonOptions,
            ct);

    public Task<SessionDetail?> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
        _http.GetFromJsonAsync<SessionDetail>($"/sessions/{sessionId}", JsonOptions, ct);

    /// <summary>
    /// Incremental session pull. Pass <paramref name="afterEntryId"/> (use empty string for an explicit full sync).
    /// </summary>
    public Task<SessionSyncResponse?> GetSessionSyncAsync(
        string sessionId,
        string? afterEntryId,
        CancellationToken ct = default)
    {
        var after = afterEntryId ?? "";
        return _http.GetFromJsonAsync<SessionSyncResponse>(
            $"/sessions/{Uri.EscapeDataString(sessionId)}?after={Uri.EscapeDataString(after)}",
            JsonOptions,
            ct);
    }

    public async Task<SessionDetail?> CreateSessionAsync(
        string agentId,
        string? title = null,
        string? workspaceId = null,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("/sessions", new CreateSessionRequest
        {
            AgentId = agentId,
            Title = title,
            WorkspaceId = workspaceId
        }, JsonOptions, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<SessionDetail>(body, JsonOptions);
    }

    public Task<WorkspaceState?> GetWorkspacesAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<WorkspaceState>("/workspaces", JsonOptions, ct);

    public async Task<WorkspaceMutationResult> OpenWorkspaceAsync(
        string path,
        string? name = null,
        bool confirm = false,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "/workspaces",
            new OpenWorkspaceRequest { Path = path, Name = name, Confirm = confirm },
            JsonOptions,
            ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<WorkspaceMutationResult>(body, JsonOptions)
               ?? new WorkspaceMutationResult();
    }

    public async Task<WorkspaceMutationResult> ActivateWorkspaceAsync(
        string id,
        bool confirm = false,
        CancellationToken ct = default)
    {
        var url = $"/workspaces/{Uri.EscapeDataString(id)}/activate"
                  + (confirm ? "?confirm=true" : "");
        using var response = await _http.PostAsync(url, null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<WorkspaceMutationResult>(body, JsonOptions)
               ?? new WorkspaceMutationResult();
    }

    public async Task<SessionDetail?> RenameSessionAsync(string sessionId, string title, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/sessions/{sessionId}/rename",
            new RenameSessionRequest { Title = title },
            JsonOptions,
            ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<SessionDetail>(body, JsonOptions);
    }

    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"/sessions/{sessionId}", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return true;
    }

    public async Task<SessionDetail?> ArchiveSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"/sessions/{sessionId}/archive", null, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<SessionDetail>(body, JsonOptions);
    }

    public async Task<SessionDetail?> RunSessionAsync(string sessionId, string message, CancellationToken ct = default)
    {
        using var response = await _longHttp.PostAsJsonAsync($"/sessions/{sessionId}/run", new RunSessionRequest
        {
            Message = message
        }, JsonOptions, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<SessionDetail>(body, JsonOptions);
    }

    /// <summary>
    /// SSE watch for session deltas (<c>event: sync</c> → <see cref="SessionSyncResponse"/>).
    /// Ends when <paramref name="cancellationToken"/> is cancelled or the server closes.
    /// </summary>
    public async Task WatchSessionEventsAsync(
        string sessionId,
        string? afterEntryId,
        Func<SessionSyncResponse, CancellationToken, Task> onSync,
        CancellationToken cancellationToken)
    {
        var after = afterEntryId ?? "";
        var path =
            $"/sessions/{Uri.EscapeDataString(sessionId)}/events?after={Uri.EscapeDataString(after)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _longHttp
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfFailed(response, err);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data = line["data:".Length..].Trim();
                if (string.Equals(eventName, "sync", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(data))
                {
                    var sync = JsonSerializer.Deserialize<SessionSyncResponse>(data, JsonOptions);
                    if (sync is not null)
                    {
                        await onSync(sync, cancellationToken).ConfigureAwait(false);
                    }
                }

                eventName = null;
            }
        }
    }

    /// <summary>Ask Runtime to cancel the in-flight AG-UI turn (Shell Stop). Safe if none.</summary>
    public async Task<bool> CancelSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"/sessions/{sessionId}/cancel", null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("cancelled", out var c) && c.GetBoolean();
        }
        catch
        {
            return response.IsSuccessStatusCode;
        }
    }

    public async Task<SessionDetail?> DecideApprovalAsync(
        string sessionId,
        string entryId,
        string decision,
        string? note = null,
        string? selected = null,
        CancellationToken ct = default)
    {
        using var response = await _longHttp.PostAsJsonAsync(
            $"/sessions/{sessionId}/approvals/{entryId}/decide",
            new DecideApprovalRequest { Decision = decision, Note = note, Selected = selected },
            JsonOptions,
            ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<SessionDetail>(body, JsonOptions);
    }

    public async Task<List<SkillSummaryDto>> GetAgentSkillsAsync(string agentId, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<SkillSummaryDto>>($"/agents/{agentId}/skills", JsonOptions, ct)
                       .ConfigureAwait(false)
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<AgentProcessHealthDto?> GetAgentHealthAsync(string agentId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"/agents/{agentId}/health", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<AgentProcessHealthDto>(body, JsonOptions);
    }

    public async Task<AgentCapabilitiesDto?> GetAgentCapabilitiesAsync(string agentId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"/agents/{agentId}/capabilities", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<AgentCapabilitiesDto>(body, JsonOptions);
    }

    public async Task<CapabilityPatchResult> PatchAgentCapabilitiesAsync(
        string agentId,
        CapabilityPatchRequest request,
        CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Patch, $"/agents/{agentId}/capabilities")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<CapabilityPatchResult>(body, JsonOptions)
               ?? new CapabilityPatchResult();
    }

    public async Task<McpConfigFile> GetMcpConfigAsync(string agentId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"/agents/{agentId}/mcp/config", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        var file = JsonSerializer.Deserialize<McpConfigFile>(body, JsonOptions)
                   ?? new McpConfigFile { Servers = [] };
        file.Servers ??= [];
        return file;
    }

    public async Task<McpSaveResult> PutMcpConfigAsync(
        string agentId,
        McpConfigFile config,
        CancellationToken ct = default)
    {
        using var response = await _http.PutAsJsonAsync($"/agents/{agentId}/mcp", config, JsonOptions, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<McpSaveResult>(body, JsonOptions)
               ?? new McpSaveResult { Saved = true, RequiresRestart = true };
    }

    public async Task<McpMutationResult> SetMcpTrustAsync(
        string agentId,
        string serverId,
        bool trusted,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
                $"/agents/{agentId}/mcp/{Uri.EscapeDataString(serverId)}/trust",
                new McpTrustRequest { Trusted = trusted },
                JsonOptions,
                ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<McpMutationResult>(body, JsonOptions)
               ?? new McpMutationResult { RequiresRestart = true, ServerId = serverId };
    }

    public async Task<McpMutationResult> DeleteMcpServerAsync(
        string agentId,
        string serverId,
        CancellationToken ct = default)
    {
        using var response = await _http
            .DeleteAsync($"/agents/{agentId}/mcp/{Uri.EscapeDataString(serverId)}", ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<McpMutationResult>(body, JsonOptions)
               ?? new McpMutationResult { RequiresRestart = true, ServerId = serverId };
    }

    public async Task<AgentInfo?> RestartAgentAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"/agents/{id}/restart", null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<AgentInfo>(body, JsonOptions);
    }

    public async Task<UploadAttachmentResult> UploadAttachmentAsync(
        string sessionId,
        string filePath,
        CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(stream);
        var name = Path.GetFileName(filePath);
        content.Add(streamContent, "file", name);
        using var response = await _http.PostAsync($"/sessions/{sessionId}/attachments", content, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<UploadAttachmentResult>(body, JsonOptions)
               ?? throw new InvalidOperationException("Empty upload response.");
    }

    public Task<LlmStatusDto?> GetLlmStatusAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<LlmStatusDto>("/llm", JsonOptions, ct);

    public async Task<LlmStatusDto> SaveLlmProfileAsync(SaveLlmProfileRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("/llm/profile", request, JsonOptions, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<LlmStatusDto>(body, JsonOptions)
               ?? throw new InvalidOperationException("Empty LLM status.");
    }

    public async Task<LlmStatusDto> SetLlmModelAsync(string model, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
                "/llm/model",
                new SetLlmModelRequest { Model = model },
                JsonOptions,
                ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<LlmStatusDto>(body, JsonOptions)
               ?? throw new InvalidOperationException("Empty LLM status.");
    }

    public async Task<LlmStatusDto> SetLlmReasoningAsync(
        bool enabled,
        string? effort = null,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
                "/llm/reasoning",
                new SetLlmReasoningRequest { Enabled = enabled, Effort = effort },
                JsonOptions,
                ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);

        return JsonSerializer.Deserialize<LlmStatusDto>(body, JsonOptions)
               ?? throw new InvalidOperationException("Empty LLM status.");
    }

    public async Task<IReadOnlyList<string>> ListLlmModelsAsync(
        string endpoint,
        string? apiKey = null,
        string? profileId = null,
        CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
                "/llm/models",
                new ListLlmModelsRequest
                {
                    Endpoint = endpoint,
                    ApiKey = apiKey,
                    ProfileId = profileId
                },
                JsonOptions,
                ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        var parsed = JsonSerializer.Deserialize<LlmModelsResponse>(body, JsonOptions);
        if (!string.IsNullOrWhiteSpace(parsed?.Error))
        {
            throw new OrbitApiException(OrbitErrorCodes.BadRequest, parsed!.Error);
        }

        return parsed?.Models ?? [];
    }

    public async Task<ReloadRunningAgentsResult> RestartRunningAgentsAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("/agents/restart-running", null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ThrowIfFailed(response, body);
        return JsonSerializer.Deserialize<ReloadRunningAgentsResult>(body, JsonOptions)
               ?? new ReloadRunningAgentsResult();
    }

    private static void ThrowIfFailed(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw OrbitApiError.ToException(body, $"HTTP {(int)response.StatusCode}");
        }
    }

    public void Dispose()
    {
        // Shared handler is owned by _longHttp (disposeHandler: true).
        _http.Dispose();
        _longHttp.Dispose();
    }
}
