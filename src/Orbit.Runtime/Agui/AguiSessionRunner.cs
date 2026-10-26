using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AGUI.Client;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Sessions;

namespace Orbit.Runtime.Agui;

public sealed class AguiSessionRunner(
    AgentRegistry registry,
    ISessionStore sessions,
    AgentSessionPersistence sessionPersistence,
    TurnTelemetry turnTelemetry,
    TurnEventProjector events,
    OrbitRuntimeAuth runtimeAuth,
    IHttpClientFactory httpClientFactory,
    ILogger<AguiSessionRunner> logger) : IAgentTurnClient, IToolSchemaCache, IApprovalSessionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _runGates = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>sessionId → agentId for in-flight AG-UI turns (drain on Stopping).</summary>
    private readonly ConcurrentDictionary<string, string> _activeRuns = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Per-session abort. ChatClientAgent turns cancel via CTS (Workflow CheckpointManager is for graphs;
    /// AgentSession serialize in ExecuteTurnAsync finally is the resume snapshot for this path).
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runAbort =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Set by <see cref="TryCancel"/> (Shell Stop); consumed when classifying OCE vs disconnect.</summary>
    private readonly ConcurrentDictionary<string, byte> _userStops = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ToolSchemaDto>> _toolSchemas =
        new(StringComparer.OrdinalIgnoreCase);

    public void Invalidate(string? agentId = null)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            _toolSchemas.Clear();
            return;
        }

        _toolSchemas.TryRemove(agentId, out _);
    }

    /// <summary>Cancel an in-flight run for this session (Shell Stop). Returns false if none.</summary>
    /// <summary>Cancel every in-flight run whose agent matches <paramref name="agentId"/>.</summary>
    public int CancelAllForAgent(string agentId)
    {
        var n = 0;
        foreach (var sessionId in _activeRuns
                     .Where(kv => string.Equals(kv.Value, agentId, StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key)
                     .ToList())
        {
            if (TryCancel(sessionId))
            {
                n++;
            }
        }

        return n;
    }

    public bool TryCancel(string sessionId)
    {
        if (!_runAbort.TryGetValue(sessionId, out var cts))
        {
            return false;
        }

        try
        {
            _userStops[sessionId] = 0;
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            _userStops.TryRemove(sessionId, out _);
            return false;
        }
    }

    private bool ConsumeUserStop(string sessionId) => _userStops.TryRemove(sessionId, out _);

    /// <summary>
    /// Cancel then wait until the run gate is free (or timeout).
    /// Returns <c>false</c> when the session is still busy after <paramref name="timeout"/> —
    /// callers must not delete/archive yet (tail appends can recreate session files).
    /// </summary>
    public async Task<bool> CancelAndDrainAsync(
        string sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        TryCancel(sessionId);
        var deadline = DateTime.UtcNow + timeout;
        while (IsSessionBusy(sessionId) && DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        var drained = !IsSessionBusy(sessionId);
        if (!drained)
        {
            logger.LogWarning(
                "CancelAndDrain timed out for session {SessionId} after {Timeout}",
                sessionId,
                timeout);
        }

        return drained;
    }

    /// <summary>Drop the per-session run gate after the session is deleted (unbounded growth otherwise).</summary>
    public void ReleaseSessionResources(string sessionId)
    {
        TryCancel(sessionId);
        if (_runAbort.TryRemove(sessionId, out var cts))
        {
            try
            {
                cts.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _activeRuns.TryRemove(sessionId, out _);
        _userStops.TryRemove(sessionId, out _);
        if (_runGates.TryRemove(sessionId, out var gate))
        {
            try
            {
                gate.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }

    public async Task<SessionDetail> RunAsync(string sessionId, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Message is required.", nameof(message));
        }

        return await WithRunGateAsync(sessionId, async () =>
        {
            var agent = RequireRunningAgent(sessionId);
            using var runLease = TrackRun(sessionId, agent.Manifest.Id);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runAbort[sessionId] = linked;

            try
            {
                sessions.Append(sessionId, SessionEvents.User(message));
                sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.AguiRunStarted, new
                {
                    agentId = agent.Manifest.Id,
                    baseUrl = agent.BaseUrl
                }));

                var turnMessages = new List<ChatMessage>
                {
                    AguiHistorySanitizer.SanitizeMessage(new ChatMessage(ChatRole.User, message))
                };

                return await ExecuteTurnAsync(sessionId, agent, turnMessages, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _runAbort.TryRemove(sessionId, out _);
                _userStops.TryRemove(sessionId, out _);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resume a tool-approval interrupt (same <see cref="AgentSession"/> as the paused run).
    /// </summary>
    public async Task<SessionDetail> ResumeApprovalAsync(
        string sessionId,
        string entryId,
        bool approved,
        CancellationToken cancellationToken,
        string? selected = null)
    {
        return await WithRunGateAsync(sessionId, async () =>
        {
            var detail = sessions.Get(sessionId)
                         ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");
            var entry = detail.Entries.FirstOrDefault(e => e.Id == entryId)
                        ?? throw new KeyNotFoundException($"Approval entry '{entryId}' not found.");
            if (!TurnEventProjector.TryReadToolApproval(entry, out var requestId, out var callId, out var toolName, out var args))
            {
                throw new InvalidOperationException("Entry is not a tool-approval interrupt.");
            }

            // Only ask_user_* may receive selected; confirm always overwrites model prefill.
            args = OrbitAskTools.ApplySelectedOnApprove(toolName, args, selected, approved);

            var agent = RequireRunningAgent(sessionId);
            using var runLease = TrackRun(sessionId, agent.Manifest.Id);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runAbort[sessionId] = linked;

            try
            {
                FunctionCallContent toolCall = new(callId, toolName, args);
                ToolApprovalRequestContent request = new(requestId, toolCall);
                var resume = new ChatMessage(ChatRole.User, [request.CreateResponse(approved)]);

                sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.ToolApprovalResume, new
                {
                    entryId,
                    requestId,
                    toolName,
                    approved,
                    selected
                }));

                var afterTurn = await ExecuteTurnAsync(sessionId, agent, [resume], linked.Token)
                    .ConfigureAwait(false);
                // ExecuteTurnAsync swallows user-Stop OCE and returns a detail with run_interrupted;
                // surface cancel so ApprovalService does not stamp the card approved.
                ThrowIfApprovalResumeCancelled(afterTurn);

                return afterTurn;
            }
            finally
            {
                _runAbort.TryRemove(sessionId, out _);
                _userStops.TryRemove(sessionId, out _);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the latest terminal run marker is <c>run_interrupted</c> with reason cancelled.
    /// </summary>
    internal static bool TurnEndedAsUserCancelled(SessionDetail detail)
    {
        for (var i = detail.Entries.Count - 1; i >= 0; i--)
        {
            var entry = detail.Entries[i];
            if (!string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = SessionPayloadJson.ReadString(entry, "kind");
            if (string.Equals(kind, SessionMetaKinds.RunInterrupted, StringComparison.OrdinalIgnoreCase))
            {
                var reason = SessionPayloadJson.ReadString(entry, "reason");
                return string.Equals(reason, "cancelled", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(kind, SessionMetaKinds.AguiRunFinished, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the latest terminal marker is <c>run_interrupted</c> with <c>reason=disconnected</c>
    /// (HTTP abort / transport drop — not Shell Stop).
    /// </summary>
    internal static bool TurnEndedAsDisconnected(SessionDetail detail)
    {
        for (var i = detail.Entries.Count - 1; i >= 0; i--)
        {
            var entry = detail.Entries[i];
            if (!string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = SessionPayloadJson.ReadString(entry, "kind");
            if (string.Equals(kind, SessionMetaKinds.RunInterrupted, StringComparison.OrdinalIgnoreCase))
            {
                var reason = SessionPayloadJson.ReadString(entry, "reason");
                return string.Equals(reason, "disconnected", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(kind, SessionMetaKinds.AguiRunFinished, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Resume wiring: cancelled/disconnected turn must become <see cref="OperationCanceledException"/>
    /// so <c>ApprovalService</c> stamps Failed instead of Approved.
    /// </summary>
    internal static void ThrowIfApprovalResumeCancelled(SessionDetail afterTurn)
    {
        if (TurnEndedAsUserCancelled(afterTurn) || TurnEndedAsDisconnected(afterTurn))
        {
            throw new OperationCanceledException(
                "Approval resume was cancelled before the tool finished.");
        }
    }

    /// <summary>
    /// Wait until no in-flight runs remain for <paramref name="agentId"/>, or until timeout.
    /// </summary>
    public async Task<bool> DrainAgentAsync(string agentId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasActiveRun(agentId))
            {
                return true;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return !HasActiveRun(agentId);
    }

    public bool HasActiveRun(string agentId) =>
        _activeRuns.Values.Any(id => string.Equals(id, agentId, StringComparison.OrdinalIgnoreCase));

    private IDisposable TrackRun(string sessionId, string agentId)
    {
        _activeRuns[sessionId] = agentId;
        return new RunLease(_activeRuns, sessionId);
    }

    private sealed class RunLease(ConcurrentDictionary<string, string> map, string sessionId) : IDisposable
    {
        public void Dispose() => map.TryRemove(sessionId, out _);
    }

    /// <summary>True when a turn or resume currently holds the session run gate.</summary>
    public bool IsSessionBusy(string sessionId) =>
        _runGates.TryGetValue(sessionId, out var gate) && gate.CurrentCount == 0;

    private async Task<SessionDetail> WithRunGateAsync(
        string sessionId,
        Func<Task<SessionDetail>> action,
        CancellationToken cancellationToken)
    {
        var gate = _runGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new OrbitApiException(
                OrbitErrorCodes.SessionBusy,
                "session_busy: Session already running.");
        }

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private AgentRegistry.TrackedAgent RequireRunningAgent(string sessionId)
    {
        var detail = sessions.Get(sessionId)
                     ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        if (!registry.TryGet(detail.Header.AgentId, out var agent))
        {
            throw new InvalidOperationException($"Unknown agent '{detail.Header.AgentId}'.");
        }

        if (agent.Lifecycle is AgentLifecycle.Stopping)
        {
            throw new InvalidOperationException($"Agent '{agent.Manifest.Id}' is stopping; refusing new runs.");
        }

        if (agent.Lifecycle != AgentLifecycle.Running || string.IsNullOrWhiteSpace(agent.BaseUrl))
        {
            throw new InvalidOperationException($"Agent '{agent.Manifest.Id}' is not running. Load it first.");
        }

        EnsureWorkspaceAligned(detail.Header, agent);
        return agent;
    }

    /// <summary>
    /// Session snapshot vs agent process workspace must match when policy != none.
    /// </summary>
    public static void EnsureWorkspaceAligned(SessionHeader header, AgentRegistry.TrackedAgent agent)
    {
        if (OrbitWorkspacePolicy.IsNone(agent.Manifest.Workspace))
        {
            return;
        }

        var sessionPath = header.WorkspacePath;
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            if (OrbitWorkspacePolicy.IsRequired(agent.Manifest.Workspace))
            {
                throw new OrbitApiException(
                    OrbitErrorCodes.WorkspaceRequired,
                    "workspace_required: session has no workspace snapshot; create a new chat after opening a folder.");
            }

            return;
        }

        if (WorkspacePathEquals(sessionPath, agent.StartedWorkspacePath))
        {
            return;
        }

        throw new OrbitApiException(
            OrbitErrorCodes.WorkspaceMismatch,
            $"workspace_mismatch: session workspace '{sessionPath}' differs from agent '{agent.Manifest.Id}' " +
            $"started at '{agent.StartedWorkspacePath ?? "(none)"}'. Restart the agent onto this session's workspace, " +
            "or continue in a session created under the current folder.");
    }

    public static bool WorkspacePathEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim()),
                Path.GetFullPath(b.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public int CountActiveRuns(IEnumerable<string>? agentIds = null)
    {
        if (agentIds is null)
        {
            return _activeRuns.Count;
        }

        var set = new HashSet<string>(agentIds, StringComparer.OrdinalIgnoreCase);
        return _activeRuns.Count(kv => set.Contains(kv.Value));
    }

    public async Task<SessionDetail> ExecuteTurnAsync(
        string sessionId,
        AgentRegistry.TrackedAgent agent,
        IList<ChatMessage> turnMessages,
        CancellationToken cancellationToken)
    {
        using var http = httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(agent.BaseUrl!.TrimEnd('/') + "/");
        // No hard 5-min cap: Research pipeline alone budgets 10 min. Prefer Stop (_runAbort)
        // and optional ORBIT_TURN_TIMEOUT_MINUTES for an explicit ceiling.
        http.Timeout = ResolveTurnHttpTimeout();
        var runId = Guid.NewGuid().ToString("N");
        http.DefaultRequestHeaders.Remove(OrbitWorkspaceHeaders.RunId);
        http.DefaultRequestHeaders.Remove(OrbitWorkspaceHeaders.SessionId);
        http.DefaultRequestHeaders.TryAddWithoutValidation(OrbitWorkspaceHeaders.RunId, runId);
        http.DefaultRequestHeaders.TryAddWithoutValidation(OrbitWorkspaceHeaders.SessionId, sessionId);
        if (!string.IsNullOrWhiteSpace(runtimeAuth.AgentToken))
        {
            http.DefaultRequestHeaders.Remove(OrbitLocalAuth.HeaderName);
            http.DefaultRequestHeaders.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, runtimeAuth.AgentToken);
        }

        var sessionHeader = sessions.Get(sessionId)?.Header;
        if (sessionHeader is not null)
        {
            OrbitWorkspaceHeaders.ApplyTo(http.DefaultRequestHeaders, sessionHeader);
        }

        var chatClient = new AGUIChatClient(new AGUIChatClientOptions(http, "/ag-ui"));
        // Client-side session history uses the same ContextWindow strategy as Agent Hosting.
#pragma warning disable MEAI001 // InMemoryChatHistoryProviderOptions experimental
#pragma warning disable MAAI001 // Compaction AsChatReducer experimental
        var compaction = new ContextWindowCompactionStrategy(
            maxContextWindowTokens: ResolveContextWindowTokens(),
            maxOutputTokens: ResolveMaxOutputTokens());
        var historyProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = compaction.AsChatReducer(),
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.BeforeMessagesRetrieval
        });
#pragma warning restore MAAI001
#pragma warning restore MEAI001
        var remoteAgent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatHistoryProvider = historyProvider,
            UseProvidedChatClientAsIs = true
        });

        var agentSession = await sessionPersistence.LoadOrCreateAsync(
                remoteAgent,
                historyProvider,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);

        var schemas = await LoadToolSchemasAsync(agent, http, cancellationToken).ConfigureAwait(false);
        var prior = sessions.Get(sessionId);
        var catalogAgent = prior?.Header.ToolCatalogAgentId;
        if (schemas.Count > 0
            && !string.Equals(catalogAgent, agent.Manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.ToolCatalog, new
            {
                agentId = agent.Manifest.Id,
                tools = schemas.Values.Select(s => new
                {
                    s.Name,
                    s.Description,
                    parameters = s.Parameters
                }).ToList()
            }));
            sessions.MarkToolCatalog(sessionId, agent.Manifest.Id);
        }

        var assistantText = new StringBuilder();
        var thinkingText = new StringBuilder();
        var turnStartedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? firstTokenAt = null;
        var toolStartedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var toolCount = 0;

        string? interruptReason = null;
        Exception? failure = null;
        try
        {
            await foreach (var update in remoteAgent
                               .RunStreamingAsync(turnMessages, agentSession, cancellationToken: cancellationToken)
                               .ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            firstTokenAt ??= DateTimeOffset.UtcNow;
                            assistantText.Append(text.Text);
                            break;

                        case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                            firstTokenAt ??= DateTimeOffset.UtcNow;
                            thinkingText.Append(reasoning.Text);
                            break;

                        case FunctionCallContent call:
                            {
                                events.FlushThinking(sessionId, thinkingText);
                                var started = DateTimeOffset.UtcNow;
                                if (!string.IsNullOrWhiteSpace(call.CallId))
                                {
                                    toolStartedAt[call.CallId] = started;
                                }

                                // Schema lives in once-per-session tool_catalog.
                                sessions.Append(sessionId, SessionEvents.ToolCall(
                                    call.CallId,
                                    call.Name,
                                    AguiHistorySanitizer.ToStorageArgs(call.Arguments),
                                    schema: null,
                                    startedAt: started));
                                toolCount++;
                                break;
                            }

                        case FunctionResultContent result:
                            {
                                var ended = DateTimeOffset.UtcNow;
                                DateTimeOffset? started = null;
                                long? durationMs = null;
                                if (!string.IsNullOrWhiteSpace(result.CallId)
                                    && toolStartedAt.TryGetValue(result.CallId, out var s))
                                {
                                    started = s;
                                    durationMs = (long)Math.Max(0, (ended - s).TotalMilliseconds);
                                }

                                var resultText = result.Result?.ToString();
                                sessions.Append(sessionId, SessionEvents.ToolResult(
                                    result.CallId,
                                    resultText,
                                    startedAt: started,
                                    endedAt: ended,
                                    durationMs: durationMs));
                                break;
                            }

                        case ToolApprovalRequestContent approval:
                            events.FlushThinking(sessionId, thinkingText);
                            events.AppendToolApproval(sessionId, approval);
                            break;

                        case UsageContent usage:
                            events.AppendUsage(sessionId, usage.Details);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout (ORBIT_TURN_TIMEOUT_MINUTES) or other non-user abort — not Stop.
            var budget = DescribeTurnHttpTimeout();
            failure = new TimeoutException($"AG-UI turn timed out ({budget}).");
            logger.LogWarning(
                "AG-UI run timed out for session {SessionId} ({Budget})",
                sessionId,
                budget);
            sessions.Append(sessionId, SessionEvents.Error($"Turn timed out ({budget})."));
        }
        catch (OperationCanceledException) when (ConsumeUserStop(sessionId))
        {
            interruptReason = "cancelled";
            logger.LogInformation("AG-UI run cancelled by user Stop for session {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            // Request CT / transport abort without TryCancel — not Shell Stop.
            _userStops.TryRemove(sessionId, out _);
            interruptReason = "disconnected";
            logger.LogInformation("AG-UI run disconnected for session {SessionId}", sessionId);
        }
        catch (HttpRequestException ex)
        {
            interruptReason = "disconnected";
            logger.LogWarning(ex, "AG-UI transport failed for session {SessionId}", sessionId);
            sessions.Append(sessionId, SessionEvents.Error(ex.Message));
        }
        catch (Exception ex)
        {
            failure = ex;
            logger.LogError(ex, "AG-UI run failed for session {SessionId}", sessionId);
            sessions.Append(sessionId, SessionEvents.Error(ex.Message));
        }
        finally
        {
            try
            {
                await sessionPersistence.SaveAsync(remoteAgent, agentSession, sessionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception saveEx)
            {
                logger.LogWarning(saveEx, "Failed to persist AgentSession for {SessionId}", sessionId);
            }

            // Exactly one terminal run marker — including error paths (avoids permanent "running").
            events.FlushThinking(sessionId, thinkingText);

            var turnEndedAt = DateTimeOffset.UtcNow;
            if (assistantText.Length > 0)
            {
                sessions.Append(sessionId, SessionEvents.Assistant(assistantText.ToString()));
            }

            sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.TurnTiming, new
            {
                startedAt = turnStartedAt.ToString("O"),
                endedAt = turnEndedAt.ToString("O"),
                durationMs = (long)Math.Max(0, (turnEndedAt - turnStartedAt).TotalMilliseconds),
                firstTokenAt = firstTokenAt?.ToString("O"),
                ttftMs = firstTokenAt is { } ft
                    ? (long)Math.Max(0, (ft - turnStartedAt).TotalMilliseconds)
                    : (long?)null,
                toolCalls = toolCount,
                cancelled = string.Equals(interruptReason, "cancelled", StringComparison.Ordinal),
                disconnected = string.Equals(interruptReason, "disconnected", StringComparison.Ordinal),
                failed = failure is not null
            }));

            try
            {
                await turnTelemetry.MergeAsync(
                        sessionId,
                        agent,
                        http,
                        runId,
                        toolStartedAt.Keys.ToList(),
                        turnStartedAt,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception telemetryEx)
            {
                logger.LogDebug(telemetryEx, "Turn telemetry merge failed for {SessionId}", sessionId);
            }

            if (interruptReason is not null)
            {
                sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.RunInterrupted, new
                {
                    reason = interruptReason,
                    agentId = agent.Manifest.Id,
                    at = DateTimeOffset.UtcNow
                }));
            }
            else if (failure is not null)
            {
                sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.RunInterrupted, new
                {
                    reason = "error",
                    agentId = agent.Manifest.Id,
                    error = failure.Message,
                    at = DateTimeOffset.UtcNow
                }));
            }
            else
            {
                sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.AguiRunFinished));
            }
        }

        if (failure is not null)
        {
            throw failure;
        }

        return sessions.Get(sessionId)
               ?? throw new InvalidOperationException("Session disappeared after run.");
    }

    private async Task<IReadOnlyDictionary<string, ToolSchemaDto>> LoadToolSchemasAsync(
        AgentRegistry.TrackedAgent agent,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        if (_toolSchemas.TryGetValue(agent.Manifest.Id, out var cached))
        {
            return cached;
        }

        try
        {
            var list = await http.GetFromJsonAsync<List<ToolSchemaDto>>(
                    "tools",
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false) ?? [];
            var map = list
                .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _toolSchemas[agent.Manifest.Id] = map;
            return map;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Tool schema catalog unavailable for {AgentId}", agent.Manifest.Id);
            return new Dictionary<string, ToolSchemaDto>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int ResolveContextWindowTokens() =>
        OrbitLlmContextWindow.Resolve(modelId: null);

    private static int ResolveMaxOutputTokens() =>
        OrbitLlmContextWindow.ResolveMaxOutputTokens();

    /// <summary>
    /// Default infinite (user Stop + agent-side budgets). Set <c>ORBIT_TURN_TIMEOUT_MINUTES</c>
    /// to a positive integer for an explicit HttpClient ceiling.
    /// </summary>
    internal static TimeSpan ResolveTurnHttpTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("ORBIT_TURN_TIMEOUT_MINUTES");
        if (int.TryParse(raw, out var minutes) && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return Timeout.InfiniteTimeSpan;
    }

    private static string DescribeTurnHttpTimeout()
    {
        var timeout = ResolveTurnHttpTimeout();
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return "non-user cancel";
        }

        return $"{(int)timeout.TotalMinutes} min ORBIT_TURN_TIMEOUT_MINUTES";
    }

}
