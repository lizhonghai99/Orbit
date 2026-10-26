using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Process-local ring of tool invocation spans produced by <see cref="ChatClientFactory"/> FunctionInvoker.
/// Runtime pulls by <c>runId</c> (preferred) after an AG-UI turn.
/// </summary>
public static class InvocationTelemetryStore
{
    private const int MaxRetained = 256;
    private static readonly ConcurrentQueue<ToolInvocationSpanDto> Spans = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static void Record(ToolInvocationSpanDto span)
    {
        Spans.Enqueue(span);
        while (Spans.Count > MaxRetained && Spans.TryDequeue(out _))
        {
            // trim
        }
    }

    public static IReadOnlyList<ToolInvocationSpanDto> Snapshot(
        DateTimeOffset? sinceUtc = null,
        string? runId = null,
        string? sessionId = null)
    {
        var all = Spans.ToArray();
        IEnumerable<ToolInvocationSpanDto> q = all;

        if (!string.IsNullOrWhiteSpace(runId))
        {
            q = q.Where(s => string.Equals(s.RunId, runId, StringComparison.Ordinal));
        }
        else if (!string.IsNullOrWhiteSpace(sessionId))
        {
            q = q.Where(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        }
        else if (sinceUtc is not null)
        {
            q = q.Where(s => s.EndedAt >= sinceUtc.Value || s.StartedAt >= sinceUtc.Value);
        }

        return q.ToList();
    }

    /// <summary>Return matching spans and remove them from the ring (avoid double-claim across sessions).</summary>
    public static IReadOnlyList<ToolInvocationSpanDto> Claim(
        string? runId = null,
        string? sessionId = null,
        DateTimeOffset? sinceUtc = null,
        IReadOnlyCollection<string>? callIds = null)
    {
        var matched = Snapshot(sinceUtc, runId, sessionId);
        if (callIds is { Count: > 0 })
        {
            var set = new HashSet<string>(callIds.Where(c => !string.IsNullOrWhiteSpace(c)), StringComparer.Ordinal);
            matched = matched
                .Where(s => s.CallId is null || set.Contains(s.CallId))
                .ToList();
        }

        if (matched.Count == 0)
        {
            return matched;
        }

        var removeKeys = new HashSet<string>(
            matched.Select(SpanKey),
            StringComparer.Ordinal);

        var kept = new List<ToolInvocationSpanDto>();
        while (Spans.TryDequeue(out var span))
        {
            if (!removeKeys.Contains(SpanKey(span)))
            {
                kept.Add(span);
            }
        }

        foreach (var span in kept)
        {
            Spans.Enqueue(span);
        }

        return matched;
    }

    public static async ValueTask<object?> InvokeAndRecordAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var callId = context.CallContent?.CallId;
        var name = context.Function.Name ?? "(tool)";
        object? result = null;
        string? error = null;

        JsonElement? argsEl = null;
        try
        {
            if (context.Arguments is { Count: > 0 })
            {
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in context.Arguments)
                {
                    dict[kv.Key] = kv.Value;
                }

                argsEl = JsonSerializer.SerializeToElement(dict, JsonOptions);
            }
        }
        catch
        {
            // best-effort
        }

        try
        {
            // Enforce capability profile before real InvokeAsync (schema stays visible).
            if (TryBlockDisabled(name, argsEl, out var blockedJson, out var blockError))
            {
                result = blockedJson;
                error = blockError;
                return result;
            }

            result = await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            var ended = DateTimeOffset.UtcNow;
            string? preview = null;
            try
            {
                if (result is not null)
                {
                    preview = result switch
                    {
                        string s => Truncate(s, 800),
                        JsonElement je => Truncate(je.GetRawText(), 800),
                        _ => Truncate(JsonSerializer.Serialize(result, JsonOptions), 800)
                    };
                }
            }
            catch
            {
                preview = result?.ToString();
            }

            // Schema lives in session tool_catalog — keep spans lean.
            Record(new ToolInvocationSpanDto
            {
                CallId = callId,
                Name = name,
                Description = context.Function.Description,
                StartedAt = started,
                EndedAt = ended,
                DurationMs = (long)Math.Max(0, (ended - started).TotalMilliseconds),
                ParametersSchema = null,
                Arguments = argsEl,
                ResultPreview = preview,
                Error = error,
                Iteration = context.Iteration,
                RunId = OrbitRunContext.RunId,
                SessionId = OrbitRunContext.SessionId
            });
        }
    }

    private static bool TryBlockDisabled(
        string toolName,
        JsonElement? argsEl,
        out string blockedJson,
        out string errorCode)
    {
        blockedJson = "";
        errorCode = "";

        if (CapabilityProfile.Instance.IsToolDisabled(toolName))
        {
            blockedJson = JsonSerializer.Serialize(new
            {
                ok = false,
                error = "tool_disabled",
                tool = toolName
            }, JsonOptions);
            errorCode = "tool_disabled";
            return true;
        }

        // Only the Skills provider loader — not every tool whose name happens to contain "skill".
        if (IsSkillLoaderTool(toolName)
            && TryGetSkillId(argsEl, out var skillId)
            && CapabilityProfile.Instance.IsSkillDisabled(skillId))
        {
            blockedJson = JsonSerializer.Serialize(new
            {
                ok = false,
                error = "skill_disabled",
                skill = skillId
            }, JsonOptions);
            errorCode = "skill_disabled";
            return true;
        }

        return false;
    }

    private static bool IsSkillLoaderTool(string toolName) =>
        string.Equals(toolName, "load_skill", StringComparison.OrdinalIgnoreCase)
        || string.Equals(toolName, "skills_load", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSkillId(JsonElement? argsEl, out string skillId)
    {
        skillId = "";
        if (argsEl is not { ValueKind: JsonValueKind.Object } obj)
        {
            return false;
        }

        // AgentSkillsProvider uses skillName (primary). One short alias only.
        foreach (var key in new[] { "skillName", "name" })
        {
            if (!obj.TryGetProperty(key, out var el))
            {
                // case-insensitive fallback
                el = default;
                foreach (var prop in obj.EnumerateObject())
                {
                    if (prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        el = prop.Value;
                        break;
                    }
                }

                if (el.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }
            }

            var text = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                skillId = text.Trim();
                return true;
            }
        }

        return false;
    }

    private static string SpanKey(ToolInvocationSpanDto s) =>
        $"{s.RunId}|{s.CallId}|{s.Name}|{s.StartedAt:O}|{s.EndedAt:O}";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
