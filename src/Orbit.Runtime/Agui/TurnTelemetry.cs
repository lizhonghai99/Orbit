using System.Net.Http.Json;
using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Sessions;

namespace Orbit.Runtime.Agui;

/// <summary>Pull Agent FunctionInvoker spans and append lean tool_invocation metas.</summary>
public sealed class TurnTelemetry(ISessionStore sessions, ILogger<TurnTelemetry> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task MergeAsync(
        string sessionId,
        AgentRegistry.TrackedAgent agent,
        HttpClient http,
        string runId,
        IReadOnlyList<string> callIds,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var url =
                $"telemetry/invocations?claim=true&runId={Uri.EscapeDataString(runId)}"
                + $"&sessionId={Uri.EscapeDataString(sessionId)}"
                + $"&since={Uri.EscapeDataString(sinceUtc.UtcDateTime.ToString("O"))}";
            var spans = await http.GetFromJsonAsync<List<ToolInvocationSpanDto>>(url, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];

            var callSet = new HashSet<string>(
                callIds.Where(c => !string.IsNullOrWhiteSpace(c)),
                StringComparer.Ordinal);
            foreach (var span in spans)
            {
                if (span.CallId is not null
                    && callSet.Count > 0
                    && !callSet.Contains(span.CallId)
                    && !string.Equals(span.RunId, runId, StringComparison.Ordinal))
                {
                    continue;
                }

                sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.ToolInvocation, new
                {
                    source = "agent",
                    agentId = agent.Manifest.Id,
                    runId,
                    callId = span.CallId,
                    name = span.Name,
                    description = span.Description,
                    startedAt = span.StartedAt.ToString("O"),
                    endedAt = span.EndedAt.ToString("O"),
                    durationMs = span.DurationMs,
                    arguments = span.Arguments,
                    resultPreview = span.ResultPreview,
                    error = span.Error,
                    iteration = span.Iteration
                }));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Agent invocation telemetry unavailable for {AgentId}", agent.Manifest.Id);
        }
    }
}
