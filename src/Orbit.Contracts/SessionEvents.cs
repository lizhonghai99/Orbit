using System.Text.Json;

namespace Orbit.Contracts;

/// <summary>
/// Versioned session event contract. Disk format stays Type + Payload JSONL;
/// all writers must go through <see cref="SessionEvents"/> so Runtime owns the shape.
/// </summary>
public static class SessionEventTypes
{
    public const int SchemaVersion = 1;

    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Thinking = "thinking";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
    public const string Approval = "approval";
    public const string Error = "error";
    public const string Meta = "meta";

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        User, Assistant, Thinking, ToolCall, ToolResult, Approval, Error, Meta
    };

    public static bool IsKnown(string? type) =>
        !string.IsNullOrWhiteSpace(type) && Known.Contains(type.Trim());

    /// <summary>Types allowed on public POST /entries (Shell may only inject user text).</summary>
    public static bool IsPubliclyAppendable(string? type) =>
        string.Equals(type?.Trim(), User, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Well-known <c>meta.kind</c> values (not exhaustive for forward-compat reads).</summary>
public static class SessionMetaKinds
{
    public const string SessionCreated = "session_created";
    public const string AguiRunStarted = "agui_run_started";
    public const string AguiRunFinished = "agui_run_finished";
    public const string RunInterrupted = "run_interrupted";
    public const string Usage = "usage";
    public const string ToolApprovalResume = "tool_approval_resume";
    public const string ApprovalDecision = "approval_decision";
    public const string ApprovalApplied = "approval_applied";
    public const string ApprovalFailed = "approval_failed";
    public const string ApprovalQueue = "approval_queue";
    public const string ApprovalPromoted = "approval_promoted";
    public const string AttachmentUploaded = "attachment_uploaded";
    public const string ToolCatalog = "tool_catalog";
    public const string TurnTiming = "turn_timing";
    /// <summary>Agent-side FunctionInvoker span (precise duration + schema).</summary>
    public const string ToolInvocation = "tool_invocation";
    /// <summary>Session archived; payload includes summary text.</summary>
    public const string SessionArchived = "session_archived";
    /// <summary>Capability profile changed (skills/tools toggles); payload before/after/source.</summary>
    public const string CapabilityChanged = "capability_changed";
    /// <summary>MCP server config changed (add/trust/delete); payload action/serverId/source.</summary>
    public const string McpChanged = "mcp_changed";
    /// <summary>Research pipeline stage (Scope/Outline/Draft); payload stage + optional detail.</summary>
    public const string ResearchStage = "research_stage";
}


/// <summary>Build + validate append requests. Single write surface for Runtime.</summary>
public static class SessionEvents
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppendEntryRequest User(string text, string? parentId = null) =>
        New(SessionEventTypes.User, new { v = SessionEventTypes.SchemaVersion, text }, parentId);

    public static AppendEntryRequest Assistant(string text, string? parentId = null) =>
        New(SessionEventTypes.Assistant, new { v = SessionEventTypes.SchemaVersion, text }, parentId);

    public static AppendEntryRequest Thinking(string text, string? parentId = null) =>
        New(SessionEventTypes.Thinking, new { v = SessionEventTypes.SchemaVersion, text }, parentId);

    public static AppendEntryRequest Error(string message, string? parentId = null) =>
        New(SessionEventTypes.Error, new { v = SessionEventTypes.SchemaVersion, message }, parentId);

    public static AppendEntryRequest ToolCall(
        string callId,
        string name,
        object? arguments,
        string? parentId = null,
        object? schema = null,
        DateTimeOffset? startedAt = null) =>
        New(SessionEventTypes.ToolCall, new
        {
            v = SessionEventTypes.SchemaVersion,
            callId,
            name,
            arguments,
            schema,
            startedAt = (startedAt ?? DateTimeOffset.UtcNow).ToString("O")
        }, parentId);

    public static AppendEntryRequest ToolResult(
        string callId,
        string? result,
        string? parentId = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null,
        long? durationMs = null) =>
        New(SessionEventTypes.ToolResult, new
        {
            v = SessionEventTypes.SchemaVersion,
            callId,
            result,
            startedAt = startedAt?.ToString("O"),
            endedAt = (endedAt ?? DateTimeOffset.UtcNow).ToString("O"),
            durationMs
        }, parentId);

    public static AppendEntryRequest Approval(object payload, string? parentId = null)
    {
        // Caller supplies approval fields; we stamp schema version.
        var element = MergeVersion(JsonSerializer.SerializeToElement(payload, JsonOptions));
        return new AppendEntryRequest
        {
            ParentId = parentId,
            Type = SessionEventTypes.Approval,
            Payload = element
        };
    }

    public static AppendEntryRequest Meta(string kind, object? fields = null, string? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("meta.kind is required.", nameof(kind));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", SessionEventTypes.SchemaVersion);
            writer.WriteString("kind", kind.Trim());
            if (fields is not null)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(fields, JsonOptions));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("v") || prop.NameEquals("kind"))
                    {
                        continue;
                    }

                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return new AppendEntryRequest
        {
            ParentId = parentId,
            Type = SessionEventTypes.Meta,
            Payload = JsonSerializer.Deserialize<JsonElement>(stream.ToArray())
        };
    }

    public static void Validate(AppendEntryRequest request)
    {
        var type = request.Type?.Trim() ?? "";
        if (!SessionEventTypes.IsKnown(type))
        {
            throw new ArgumentException(
                $"Unknown session event type '{type}'. Known: user|assistant|thinking|tool_call|tool_result|approval|error|meta.");
        }

        if (request.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            // Allow empty payload only for forward-compat reads; writers must send objects.
            throw new ArgumentException($"Session event '{type}' requires an object payload.");
        }

        switch (type.ToLowerInvariant())
        {
            case SessionEventTypes.User:
            case SessionEventTypes.Assistant:
            case SessionEventTypes.Thinking:
                RequireAnyString(payload, "text", "content", "message");
                break;
            case SessionEventTypes.Error:
                RequireAnyString(payload, "message", "text");
                break;
            case SessionEventTypes.ToolCall:
                RequireString(payload, "callId");
                RequireString(payload, "name");
                break;
            case SessionEventTypes.ToolResult:
                RequireString(payload, "callId");
                break;
            case SessionEventTypes.Approval:
                RequireString(payload, "status");
                break;
            case SessionEventTypes.Meta:
                RequireString(payload, "kind");
                break;
        }
    }

    private static AppendEntryRequest New(string type, object payload, string? parentId) =>
        new()
        {
            ParentId = parentId,
            Type = type,
            Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };

    private static JsonElement MergeVersion(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.SerializeToElement(new { v = SessionEventTypes.SchemaVersion }, JsonOptions);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", SessionEventTypes.SchemaVersion);
            foreach (var prop in payload.EnumerateObject())
            {
                if (prop.NameEquals("v"))
                {
                    continue;
                }

                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static void RequireString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var el)
            || el.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(el.GetString()))
        {
            throw new ArgumentException($"Session event payload missing required string '{name}'.");
        }
    }

    private static void RequireAnyString(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (payload.TryGetProperty(name, out var el)
                && el.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(el.GetString()))
            {
                return;
            }
        }

        throw new ArgumentException(
            $"Session event payload needs one of: {string.Join('|', names)}.");
    }
}
