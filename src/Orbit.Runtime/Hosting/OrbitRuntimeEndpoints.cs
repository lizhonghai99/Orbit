using System.Net.Http.Json;
using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Agui;
using Orbit.Runtime.Approvals;
using Orbit.Runtime.Attachments;
using Orbit.Runtime.Llm;
using Orbit.Runtime.Sessions;
using Orbit.Runtime.Workspaces;

namespace Orbit.Runtime.Hosting;

/// <summary>HTTP route table for Orbit.Runtime (kept out of Program.cs).</summary>
internal static class OrbitRuntimeEndpoints
{
    public static void Map(WebApplication app, DateTimeOffset startedAt)
    {
        // Broad catch (Exception) → log + generic 500; typed catches stay message-preserving.
        IResult Fail(Exception ex, string operation) =>
            OrbitApiResults.FromException(ex, app.Logger, operation);

        app.MapGet("/health", (RepoPaths repo) => Results.Ok(new RuntimeHealth
        {
            Status = "ok",
            Version = OrbitProductVersion.Current,
            StartedAt = startedAt,
            RepoRoot = repo.RepoRoot
        }));

        app.MapGet("/agents", (AgentRegistry registry) => Results.Ok(registry.List()));

        app.MapGet("/agents/manifest-errors", (AgentRegistry registry) =>
            Results.Ok(registry.ManifestLoadErrors));

        app.MapPost("/agents/reload", (AgentRegistry registry) =>
        {
            registry.Reload();
            return Results.Ok(new
            {
                agents = registry.List(),
                manifestErrors = registry.ManifestLoadErrors
            });
        });

        // Restart every Running agent process (LLM / reasoning env pick-up).
        app.MapPost("/agents/restart-running", async (IAgentSupervisor supervisor, CancellationToken ct) =>
        {
            var result = await supervisor.RestartRunningAsync(ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        app.MapGet("/llm", (LlmSettingsService llm) => Results.Ok(llm.GetStatus()));

        app.MapPost("/llm/profile", (SaveLlmProfileRequest request, LlmSettingsService llm) =>
        {
            try
            {
                return Results.Ok(llm.SaveProfile(request));
            }
            catch (Exception ex)
            {
                return Fail(ex, "save llm profile");
            }
        });

        app.MapPost("/llm/model", (SetLlmModelRequest request, LlmSettingsService llm) =>
        {
            try
            {
                return Results.Ok(llm.SetActiveModel(request.Model));
            }
            catch (Exception ex)
            {
                return Fail(ex, "set llm model");
            }
        });

        app.MapPost("/llm/reasoning", (SetLlmReasoningRequest request, LlmSettingsService llm) =>
        {
            try
            {
                return Results.Ok(llm.SetReasoning(request.Enabled, request.Effort));
            }
            catch (Exception ex)
            {
                return Fail(ex, "set llm reasoning");
            }
        });

        app.MapPost("/llm/models", async (ListLlmModelsRequest request, LlmSettingsService llm, CancellationToken ct) =>
        {
            try
            {
                var models = await llm.ListModelsAsync(request, ct).ConfigureAwait(false);
                return Results.Ok(new LlmModelsResponse { Models = models });
            }
            catch (Exception ex)
            {
                return Results.Ok(new LlmModelsResponse { Error = ex.Message });
            }
        });

        app.MapPost("/agents/{id}/start", async (string id, IAgentSupervisor supervisor, CancellationToken ct) =>
        {
            try
            {
                var info = await supervisor.StartAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(info);
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (Exception ex)
            {
                return Fail(ex, $"start agent {id}");
            }
        });

        app.MapPost("/agents/{id}/stop", async (string id, IAgentSupervisor supervisor, CancellationToken ct) =>
        {
            try
            {
                var info = await supervisor.StopAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(info);
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
        });

        app.MapPost("/agents/{id}/restart", async (string id, IAgentSupervisor supervisor, CancellationToken ct) =>
        {
            try
            {
                var info = await supervisor.RestartAsync(id, ct).ConfigureAwait(false);
                return Results.Ok(info);
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (Exception ex)
            {
                return Fail(ex, $"restart agent {id}");
            }
        });

        app.MapGet("/workspaces", (WorkspaceService workspaces) => Results.Ok(workspaces.GetState()));

        app.MapPost("/workspaces", async (
            OpenWorkspaceRequest request,
            WorkspaceService workspaces,
            WorkspaceAgentRestarter restarter,
            CancellationToken ct) =>
        {
            try
            {
                var previewPath = Path.GetFullPath(request.Path.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                restarter.EnsureConfirmOrThrow(previewPath, request.Confirm);
                var active = workspaces.Open(request);
                return Results.Ok(await restarter.AfterMutationAsync(active, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is OrbitApiException or ArgumentException or InvalidOperationException)
            {
                return OrbitApiResults.FromException(ex);
            }
        });

        app.MapPost("/workspaces/{id}/activate", async (
            string id,
            bool? confirm,
            WorkspaceService workspaces,
            WorkspaceAgentRestarter restarter,
            CancellationToken ct) =>
        {
            try
            {
                var existing = workspaces.GetById(id)
                               ?? throw new KeyNotFoundException($"Unknown workspace '{id}'.");
                restarter.EnsureConfirmOrThrow(existing.Path, confirm == true);
                var active = workspaces.Activate(id);
                return Results.Ok(await restarter.AfterMutationAsync(active, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (
                ex is OrbitApiException or KeyNotFoundException or ArgumentException or InvalidOperationException)
            {
                return OrbitApiResults.FromException(ex);
            }
        });

        app.MapDelete("/workspaces/{id}", async (
            string id,
            bool? confirm,
            WorkspaceService workspaces,
            WorkspaceAgentRestarter restarter,
            CancellationToken ct) =>
        {
            try
            {
                var activeBefore = workspaces.GetActive();
                if (activeBefore is not null
                    && string.Equals(activeBefore.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return OrbitApiResults.Json(
                        StatusCodes.Status400BadRequest,
                        OrbitErrorCodes.WorkspaceActiveLocked,
                        "workspace_active_locked: activate another workspace before removing this one.");
                }

                workspaces.Remove(id);
                return Results.Ok(new WorkspaceMutationResult
                {
                    Active = workspaces.GetActive()
                });
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (ArgumentException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
        });

        app.MapGet("/sessions", (ISessionStore store, bool? includeArchived) =>
            Results.Ok(store.List(includeArchived ?? false)));

        MapSessionSyncEndpoints(app);
        MapSessionAppendEndpoint(app);
        MapSessionAgentMetaEndpoint(app);

        app.MapPost("/sessions", (
            CreateSessionRequest request,
            ISessionStore store,
            WorkspaceService workspaces,
            AgentRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(request.AgentId))
            {
                return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, "agentId is required");
            }

            if (!registry.TryGet(request.AgentId, out var tracked))
            {
                return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, $"Unknown agent '{request.AgentId}'.");
            }

            var workspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId)
                ? workspaces.GetActive()?.Id
                : request.WorkspaceId.Trim();
            WorkspaceRecord? workspace = null;
            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                workspace = workspaces.GetById(workspaceId);
                if (workspace is null)
                {
                    return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, $"Unknown workspace '{workspaceId}'.");
                }
            }

            if (OrbitWorkspacePolicy.IsRequired(tracked.Manifest.Workspace) && workspace is null)
            {
                return OrbitApiResults.Json(
                    StatusCodes.Status400BadRequest,
                    OrbitErrorCodes.WorkspaceRequired,
                    "workspace_required");
            }

            if (workspace is not null && !Directory.Exists(workspace.Path))
            {
                return OrbitApiResults.Json(
                    StatusCodes.Status400BadRequest,
                    OrbitErrorCodes.WorkspaceMissing,
                    $"workspace_missing: {workspace.Path}",
                    new Dictionary<string, string> { ["path"] = workspace.Path });
            }

            var create = new CreateSessionRequest
            {
                AgentId = request.AgentId,
                Title = request.Title,
                WorkspaceId = workspace?.Id,
                WorkspacePath = workspace?.Path
            };
            return Results.Ok(store.Create(create));
        });

        app.MapPost("/sessions/{sessionId}/rename", (string sessionId, RenameSessionRequest request, ISessionStore store) =>
        {
            try
            {
                return Results.Ok(store.Rename(sessionId, request.Title ?? "New chat"));
            }
            catch (KeyNotFoundException)
            {
                return OrbitApiResults.Json(StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, "Not found.");
            }
        });

        app.MapDelete("/sessions/{sessionId}", async (
            string sessionId,
            ISessionStore store,
            AgentSessionFileStore agentSessions,
            AguiSessionRunner runner,
            RepoPaths repo,
            CancellationToken ct) =>
        {
            if (!SessionIdRules.IsValid(sessionId))
            {
                return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, "Invalid session id.");
            }

            if (!await runner.CancelAndDrainAsync(sessionId, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
            {
                return OrbitApiResults.Json(
                    StatusCodes.Status409Conflict,
                    OrbitErrorCodes.SessionBusy,
                    "session_busy: in-flight turn did not drain; retry delete shortly.");
            }

            var detail = store.Get(sessionId);
            agentSessions.Delete(sessionId);
            if (detail is not null)
            {
                var attachDir = Path.Combine(repo.DataDir, "attachments", detail.Header.AgentId, detail.Header.SessionId);
                var attachmentsRoot = Path.GetFullPath(Path.Combine(repo.DataDir, "attachments"));
                var fullAttach = Path.GetFullPath(attachDir);
                if (OrbitPathNames.IsUnder(fullAttach, attachmentsRoot)
                    && Directory.Exists(fullAttach))
                {
                    try
                    {
                        Directory.Delete(fullAttach, recursive: true);
                    }
                    catch
                    {
                        // best-effort; JSONL delete still proceeds
                    }
                }
            }

            var deleted = store.Delete(sessionId);
            if (deleted)
            {
                runner.ReleaseSessionResources(sessionId);
            }

            return deleted
                ? Results.Ok(new { deleted = true, sessionId })
                : OrbitApiResults.Json(StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, "Session not found.");
        });

        app.MapPost("/sessions/{sessionId}/archive", async (
            string sessionId,
            SessionArchiver archiver,
            AguiSessionRunner runner,
            CancellationToken ct) =>
        {
            try
            {
                if (!await runner.CancelAndDrainAsync(sessionId, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
                {
                    return OrbitApiResults.Json(
                        StatusCodes.Status409Conflict,
                        OrbitErrorCodes.SessionBusy,
                        "session_busy: in-flight turn did not drain; retry archive shortly.");
                }

                return Results.Ok(await archiver.ArchiveAsync(sessionId, ct).ConfigureAwait(false));
            }
            catch (KeyNotFoundException)
            {
                return OrbitApiResults.Json(StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, "Not found.");
            }
            catch (Exception ex)
            {
                return Fail(ex, $"archive session {sessionId}");
            }
        });

        app.MapPost("/sessions/{sessionId}/run", async (string sessionId, RunSessionRequest request, AguiSessionRunner runner, CancellationToken ct) =>
        {
            try
            {
                var detail = await runner.RunAsync(sessionId, request.Message, ct).ConfigureAwait(false);
                return Results.Ok(detail);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller went away; nothing useful to write back.
                throw;
            }
            catch (Exception ex)
            {
                return Fail(ex, $"run session {sessionId}");
            }
        });

        app.MapPost("/sessions/{sessionId}/cancel", (string sessionId, AguiSessionRunner runner) =>
        {
            var cancelled = runner.TryCancel(sessionId);
            return cancelled
                ? Results.Ok(new { cancelled = true, sessionId })
                : Results.Ok(new { cancelled = false, sessionId, message = "No in-flight run." });
        });

        app.MapPost("/sessions/{sessionId}/approvals/{entryId}/decide", async (string sessionId, string entryId, DecideApprovalRequest request, ApprovalService approvals, CancellationToken ct) =>
        {
            try
            {
                var detail = await approvals.DecideAsync(sessionId, entryId, request, ct).ConfigureAwait(false);
                return Results.Ok(detail);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Fail(ex, $"decide approval {entryId} in {sessionId}");
            }
        });

        app.MapGet("/agents/{id}/skills", async (string id, AgentRegistry registry, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (!registry.TryGet(id, out var agent) || agent.Lifecycle != AgentLifecycle.Running || string.IsNullOrWhiteSpace(agent.BaseUrl))
            {
                return Results.Ok(Array.Empty<SkillSummaryDto>());
            }

            try
            {
                using var http = httpFactory.CreateClient("agent-health");
                var skills = await http.GetFromJsonAsync<List<SkillSummaryDto>>(
                    $"{agent.BaseUrl.TrimEnd('/')}/skills",
                    ct).ConfigureAwait(false);
                return Results.Ok(skills ?? []);
            }
            catch
            {
                return Results.Ok(Array.Empty<SkillSummaryDto>());
            }
        });

        app.MapGet("/agents/{id}/capabilities", async (string id, CapabilityService capabilities, CancellationToken ct) =>
            Results.Ok(await capabilities.GetAsync(id, ct).ConfigureAwait(false)));

        app.MapMethods("/agents/{id}/capabilities", ["PATCH"], async (string id, CapabilityPatchRequest? body, CapabilityService capabilities, CancellationToken ct) =>
        {
            try
            {
                var result = await capabilities.PatchAsync(id, body ?? new CapabilityPatchRequest(), ct)
                    .ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (HttpRequestException ex)
            {
                return OrbitApiResults.Json(StatusCodes.Status502BadGateway, OrbitErrorCodes.BadGateway, ex.Message);
            }
        });

        app.MapGet("/agents/{id}/mcp/config", (string id, McpConfigService mcp) =>
        {
            try
            {
                return Results.Ok(mcp.Load(id));
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (InvalidDataException ex)
            {
                return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, ex.Message, new Dictionary<string, string> { ["corrupt"] = "true" });
            }
        });

        app.MapPut("/agents/{id}/mcp", (string id, McpConfigFile? body, McpConfigService mcp) =>
        {
            if (body is null)
            {
                return OrbitApiResults.Json(
                    StatusCodes.Status400BadRequest,
                    OrbitErrorCodes.BadRequest,
                    "mcp_body_required: PUT body must be a JSON object with a 'servers' array.");
            }

            try
            {
                return Results.Ok(mcp.Put(id, body));
            }
            catch (OrbitApiException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (ArgumentException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
        });

        app.MapPost("/agents/{id}/mcp/{serverId}/trust", (string id, string serverId, McpTrustRequest? body, McpConfigService mcp) =>
        {
            try
            {
                return Results.Ok(mcp.SetTrust(id, serverId, body?.Trusted ?? false));
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
        });

        app.MapDelete("/agents/{id}/mcp/{serverId}", (string id, string serverId, McpConfigService mcp) =>
        {
            try
            {
                return Results.Ok(mcp.Delete(id, serverId));
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
        });

        app.MapGet("/agents/{id}/health", async (string id, AgentRegistry registry, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (!registry.TryGet(id, out var agent) || agent.Lifecycle != AgentLifecycle.Running || string.IsNullOrWhiteSpace(agent.BaseUrl))
            {
                return Results.Ok(new AgentProcessHealthDto
                {
                    Status = "offline",
                    AgentId = id,
                    Ready = false,
                    Chat = "offline",
                    Harness = null,
                    Skills = [],
                    Tools = null
                });
            }

            try
            {
                using var http = httpFactory.CreateClient("agent-health");
                var health = await http.GetFromJsonAsync<AgentProcessHealthDto>(
                    $"{agent.BaseUrl.TrimEnd('/')}/health",
                    ct).ConfigureAwait(false);
                return Results.Ok(health ?? new AgentProcessHealthDto { Status = "unknown", AgentId = id, Ready = false });
            }
            catch (Exception ex)
            {
                return Results.Ok(new AgentProcessHealthDto
                {
                    Status = "unreachable",
                    AgentId = id,
                    Ready = false,
                    Chat = ex.Message,
                    Harness = null,
                    Skills = [],
                    Tools = null
                });
            }
        });

        app.MapPost("/sessions/{sessionId}/attachments", async (string sessionId, HttpRequest request, AttachmentService attachments, CancellationToken ct) =>
        {
            try
            {
                if (!request.HasFormContentType)
                {
                    return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, "multipart/form-data required");
                }

                var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                {
                    return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, "file is required");
                }

                await using var stream = file.OpenReadStream();
                var result = await attachments.UploadAsync(
                    sessionId,
                    file.FileName,
                    file.ContentType ?? "application/octet-stream",
                    stream,
                    ct).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (ArgumentException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
            catch (Exception ex)
            {
                return Fail(ex, $"upload attachment {sessionId}");
            }
        });
    }

    /// <summary>
    /// Session GET sync + SSE. Extracted so tests host the same routes (not a copy).
    /// Poll stays short; heartbeat is independent so idle clients are not flushed every tick.
    /// </summary>
    internal static void MapSessionSyncEndpoints(
        IEndpointRouteBuilder app,
        TimeSpan? ssePollInterval = null,
        TimeSpan? sseHeartbeatInterval = null)
    {
        var poll = ssePollInterval ?? TimeSpan.FromMilliseconds(300);
        var heartbeat = sseHeartbeatInterval ?? TimeSpan.FromSeconds(12);

        app.MapGet("/sessions/{sessionId}", (string sessionId, string? after, ISessionStore store) =>
        {
            if (!SessionIdRules.IsValid(sessionId))
            {
                return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, "Invalid session id.");
            }

            // Incremental: ?after={entryId} → SessionSyncResponse (full snapshot when after omitted/unknown).
            if (after is not null)
            {
                var sync = store.GetSync(sessionId, after);
                return sync is null
                    ? OrbitApiResults.Json(StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, "Session not found.")
                    : Results.Ok(sync);
            }

            var detail = store.Get(sessionId);
            return detail is null
                ? OrbitApiResults.Json(StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, "Session not found.")
                : Results.Ok(detail);
        });

        // Server-sent events: push SessionSyncResponse while a turn runs (Shell cancels on turn end).
        app.MapGet("/sessions/{sessionId}/events", async (
            string sessionId,
            string? after,
            ISessionStore store,
            HttpResponse response,
            CancellationToken ct) =>
        {
            if (!SessionIdRules.IsValid(sessionId))
            {
                return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, "Invalid session id.");
            }

            if (store.Get(sessionId) is null)
            {
                return OrbitApiResults.Json(StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, "Session not found.");
            }

            // Keep the session in the hot cache for the life of this SSE subscription.
            using var livePin = (store as JsonlSessionStore)?.PinLive(sessionId);

            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers.Append("X-Accel-Buffering", "no");

            var cursor = after;
            var lastHeartbeat = DateTimeOffset.UtcNow;
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            };

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var sync = store.GetSync(sessionId, cursor);
                    if (sync is null)
                    {
                        await WriteSseAsync(response, "error", "{\"message\":\"session_gone\"}", ct)
                            .ConfigureAwait(false);
                        break;
                    }

                    if (sync.FullSnapshot || sync.Entries.Count > 0)
                    {
                        var payload = System.Text.Json.JsonSerializer.Serialize(sync, jsonOptions);
                        await WriteSseAsync(response, "sync", payload, ct).ConfigureAwait(false);
                        lastHeartbeat = DateTimeOffset.UtcNow;
                        if (sync.Entries.Count > 0)
                        {
                            cursor = sync.Entries[^1].Id;
                        }
                        else if (sync.FullSnapshot && sync.Header.CurrentEntryId is { Length: > 0 } current)
                        {
                            cursor = current;
                        }
                    }
                    else if (DateTimeOffset.UtcNow - lastHeartbeat >= heartbeat)
                    {
                        await WriteSseAsync(response, "ping", "{}", ct).ConfigureAwait(false);
                        lastHeartbeat = DateTimeOffset.UtcNow;
                    }

                    await Task.Delay(poll, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // client disconnected / Shell cancelled watch
            }

            return Results.Empty;
        });
    }

    /// <summary>Public POST /entries (Shell may only append user text). Extracted for the same test host as sync/SSE.</summary>
    internal static void MapSessionAppendEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/sessions/{sessionId}/entries", (string sessionId, AppendEntryRequest request, ISessionStore store) =>
        {
            var type = request.Type?.Trim() ?? "";
            if (!SessionEventTypes.IsPubliclyAppendable(type))
            {
                return OrbitApiResults.Json(
                    StatusCodes.Status403Forbidden,
                    OrbitErrorCodes.Forbidden,
                    $"Entry type '{type}' cannot be appended via public API. Use /run or /approvals.");
            }

            try
            {
                return Results.Ok(store.Append(sessionId, request));
            }
            catch (KeyNotFoundException)
            {
                return OrbitApiResults.Json(StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, "Not found.");
            }
        });
    }

    /// <summary>
    /// Agent-only progress channel (Research stages). Auth middleware accepts Agent token on this path only.
    /// </summary>
    internal static void MapSessionAgentMetaEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/sessions/{sessionId}/agent-meta", (string sessionId, AppendEntryRequest request, ISessionStore store) =>
        {
            if (!SessionIdRules.IsValid(sessionId))
            {
                return OrbitApiResults.Json(StatusCodes.Status400BadRequest, OrbitErrorCodes.BadRequest, "Invalid session id.");
            }

            var type = request.Type?.Trim() ?? "";
            if (!string.Equals(type, SessionEventTypes.Meta, StringComparison.OrdinalIgnoreCase))
            {
                return OrbitApiResults.Json(
                    StatusCodes.Status403Forbidden,
                    OrbitErrorCodes.Forbidden,
                    "agent-meta accepts meta entries only.");
            }

            if (request.Payload is not { } payload
                || payload.ValueKind != System.Text.Json.JsonValueKind.Object
                || !string.Equals(
                    SessionPayloadJson.ReadString(payload, "kind"),
                    SessionMetaKinds.ResearchStage,
                    StringComparison.OrdinalIgnoreCase))
            {
                return OrbitApiResults.Json(
                    StatusCodes.Status403Forbidden,
                    OrbitErrorCodes.Forbidden,
                    $"agent-meta only allows kind={SessionMetaKinds.ResearchStage}.");
            }

            try
            {
                return Results.Ok(store.Append(sessionId, request));
            }
            catch (KeyNotFoundException)
            {
                return OrbitApiResults.Json(StatusCodes.Status404NotFound, OrbitErrorCodes.NotFound, "Not found.");
            }
            catch (ArgumentException ex)
            {
                return OrbitApiResults.FromException(ex);
            }
        });
    }

    private static async Task WriteSseAsync(
        HttpResponse response,
        string eventName,
        string dataJson,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {eventName}\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync($"data: {dataJson}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
