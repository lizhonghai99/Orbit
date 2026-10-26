using System.Globalization;
using System.Text;
using System.Text.Json;
using Orbit.Contracts;
using Orbit.Shell.I18n;

namespace Orbit.Shell.ViewModels;

/// <summary>
/// Projects session JSONL into a DeepSeek-style trajectory ledger (process-complete, not chat-collapsed).
/// </summary>
public static class TrajectoryProjector
{
    private static readonly Dictionary<TrajectoryKind, (string Fg, string Soft)> KindColors = new()
    {
        [TrajectoryKind.User] = ("#2563EB", "#DBEAFE"),
        [TrajectoryKind.Assistant] = ("#7C3AED", "#EDE9FE"),
        [TrajectoryKind.Thinking] = ("#6B7280", "#F3F4F6"),
        [TrajectoryKind.Tool] = ("#C2410C", "#FFEDD5"),
        [TrajectoryKind.Approval] = ("#A16207", "#FEF3C7"),
        [TrajectoryKind.Meta] = ("#15803D", "#DCFCE7"),
        [TrajectoryKind.Error] = ("#B91C1C", "#FEE2E2"),
        [TrajectoryKind.Context] = ("#0F766E", "#CCFBF1")
    };

    public static IReadOnlyList<TrajectoryRecordVm> Project(
        IReadOnlyList<SessionEntry> entries,
        IReadOnlyDictionary<string, string>? toolSchemaHints = null)
    {
        var result = new List<TrajectoryRecordVm>();
        DateTimeOffset? runStart = null;
        // callId → schema text captured on tool_call for orphan-safe Schema tab
        var schemaByCall = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var agentSpans = IndexAgentInvocations(entries);
        var catalogSchemas = IndexToolCatalog(entries);
        var mergedHints = MergeHints(toolSchemaHints, catalogSchemas);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var type = entry.Type?.Trim().ToLowerInvariant() ?? "";

            switch (type)
            {
                case "user":
                    result.Add(MessageRecord(entry, TrajectoryKind.User, Loc.T("traj.kind.user")));
                    break;

                case "assistant":
                    result.Add(MessageRecord(entry, TrajectoryKind.Assistant, Loc.T("traj.kind.assistant")));
                    break;

                case "thinking":
                    result.Add(MessageRecord(entry, TrajectoryKind.Thinking, Loc.T("traj.kind.thinking")));
                    break;

                case "tool_call":
                    {
                        SessionEntry? paired = null;
                        if (i + 1 < entries.Count
                            && string.Equals(entries[i + 1].Type, "tool_result", StringComparison.OrdinalIgnoreCase)
                            && SameCallId(entry, entries[i + 1]))
                        {
                            paired = entries[i + 1];
                            i++;
                        }

                        var callId = SessionPayloadJson.ReadString(entry, "callId");
                        agentSpans.TryGetValue(callId ?? "", out var span);
                        var rec = ToolRecord(entry, paired, mergedHints, schemaByCall, span);
                        result.Add(rec);
                        break;
                    }

                case "tool_result":
                    result.Add(OrphanToolResult(entry, schemaByCall));
                    break;

                case "approval":
                    result.Add(ApprovalRecord(entry));
                    break;

                case "error":
                    result.Add(ErrorRecord(entry));
                    break;

                case "meta":
                    {
                        var kind = SessionPayloadJson.ReadString(entry, "kind") ?? "meta";
                        // Reverse whitelist: only skip known noise; unknown kinds stay visible as generic.
                        if (IsTrajectoryNoiseMeta(kind))
                        {
                            break;
                        }

                        if (string.Equals(kind, "agui_run_started", StringComparison.OrdinalIgnoreCase))
                        {
                            runStart = entry.Timestamp;
                            result.Add(MetaRecord(entry, kind, Loc.T("traj.meta.run_started"), null, runStart));
                        }
                        else if (string.Equals(kind, "agui_run_finished", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(kind, "run_interrupted", StringComparison.OrdinalIgnoreCase))
                        {
                            var label = string.Equals(kind, "run_interrupted", StringComparison.OrdinalIgnoreCase)
                                ? Loc.T("traj.meta.run_interrupted", SessionPayloadJson.ReadString(entry, "reason") ?? "interrupted")
                                : Loc.T("traj.meta.run_finished");
                            result.Add(MetaRecord(entry, kind, label, entry.Timestamp, runStart));
                            runStart = null;
                        }
                        else if (string.Equals(kind, "turn_timing", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(TurnTimingRecord(entry));
                        }
                        else if (string.Equals(kind, "usage", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(UsageRecord(entry));
                        }
                        else if (string.Equals(kind, "research_stage", StringComparison.OrdinalIgnoreCase))
                        {
                            var stage = SessionPayloadJson.ReadString(entry, "stage") ?? "step";
                            result.Add(MetaRecord(
                                entry,
                                kind,
                                Loc.T("traj.meta.research_stage", stage),
                                null,
                                null));
                        }
                        else
                        {
                            result.Add(MetaRecord(entry, kind, Loc.T("traj.meta.generic", kind), null, null));
                        }

                        break;
                    }

                default:
                    result.Add(Make(
                        entry.Id,
                        TrajectoryKind.Context,
                        Loc.T("traj.kind.context"),
                        type,
                        Truncate(ExtractText(entry) ?? "", 80),
                        entry.Timestamp,
                        null,
                        null,
                        type,
                        Loc.T("traj.status.recorded"),
                        PayloadPretty(entry) ?? Loc.T("traj.no_payload"),
                        "",
                        Loc.T("traj.schema_na"),
                        FormatTiming(entry.Timestamp, null, null)));
                    break;
            }
        }

        return result;
    }

    public static IReadOnlyList<TrajectoryTimelineSegmentVm> BuildTimeline(IReadOnlyList<TrajectoryRecordVm> records)
    {
        if (records.Count == 0)
        {
            return [];
        }

        return records
            .Select(r => new TrajectoryTimelineSegmentVm
            {
                ColorHex = r.ColorHex,
                Weight = Math.Max(1, r.TimelineWeight)
            })
            .ToList();
    }

    private static TrajectoryRecordVm MessageRecord(SessionEntry entry, TrajectoryKind kind, string kindLabel)
    {
        var text = ExtractText(entry) ?? "";
        var oneLine = Collapse(text);
        return Make(
            entry.Id,
            kind,
            kindLabel,
            kindLabel,
            Truncate(oneLine, 100),
            entry.Timestamp,
            null,
            null,
            kindLabel,
            Loc.T("traj.status.completed"),
            string.IsNullOrWhiteSpace(text) ? Loc.T("traj.no_content") : text,
            "",
            Loc.T("traj.schema_na"),
            FormatTiming(entry.Timestamp, null, null),
            weight: Math.Max(1, EstimateWeightFromText(text)));
    }

    private static TrajectoryRecordVm ToolRecord(
        SessionEntry call,
        SessionEntry? result,
        IReadOnlyDictionary<string, string>? hints,
        Dictionary<string, string> schemaByCall,
        SessionEntry? agentSpan)
    {
        var name = SessionPayloadJson.ReadString(call, "name")
                   ?? (agentSpan is null ? null : SessionPayloadJson.ReadString(agentSpan, "name"))
                   ?? "tool";
        var callId = SessionPayloadJson.ReadString(call, "callId") ?? call.Id;
        var argsText = FormatArguments(call);
        if (string.IsNullOrWhiteSpace(argsText) && agentSpan is not null)
        {
            argsText = FormatNamedJson(agentSpan, "arguments");
        }

        var resultText = result is null ? Loc.T("traj.no_result") : FormatToolResult(result);
        if ((result is null || resultText == Loc.T("traj.no_result"))
            && agentSpan is not null
            && !string.IsNullOrWhiteSpace(SessionPayloadJson.ReadString(agentSpan, "resultPreview")))
        {
            resultText = SessionPayloadJson.ReadString(agentSpan, "resultPreview")!;
        }

        var resultSummary = result is null && agentSpan is null
            ? "…"
            : SummarizeResult(resultText);
        var started = (agentSpan is null ? null : ReadTime(agentSpan, "startedAt"))
                      ?? ReadTime(call, "startedAt")
                      ?? call.Timestamp;
        var ended = (agentSpan is null ? null : ReadTime(agentSpan, "endedAt"))
                    ?? (result is null ? null : ReadTime(result, "endedAt") ?? result.Timestamp);
        var durationMs = (agentSpan is null ? null : ReadLong(agentSpan, "durationMs"))
                         ?? (result is null ? null : ReadLong(result, "durationMs"))
                         ?? (ended is { } e ? (long?)Math.Max(0, (e - started).TotalMilliseconds) : null);

        var schemaText = (agentSpan is null ? null : FormatSchemaFromCall(agentSpan))
                         ?? FormatSchemaFromCall(call)
                         ?? ResolveSchema(name, hints);
        schemaByCall[callId] = schemaText;

        var err = agentSpan is null ? null : SessionPayloadJson.ReadString(agentSpan, "error");
        var status = !string.IsNullOrWhiteSpace(err)
            ? Loc.T("traj.status.failed")
            : result is null && agentSpan is null
                ? Loc.T("traj.status.running")
                : Loc.T("traj.status.completed");

        var subtitle = string.IsNullOrWhiteSpace(argsText)
            ? $"→ {resultSummary}"
            : $"{Truncate(Collapse(argsText), 72)}  →  {resultSummary}";
        if (!string.IsNullOrWhiteSpace(err))
        {
            subtitle = Truncate(err, 100);
        }

        var timing = FormatTiming(started, ended, durationMs);
        if (agentSpan is not null)
        {
            timing += "\n" + Loc.T("traj.timing.source_agent");
        }

        return Make(
            call.Id,
            TrajectoryKind.Tool,
            Loc.T("traj.kind.tool"),
            name,
            subtitle,
            started,
            ended,
            durationMs,
            Loc.T("traj.level.tool", name),
            status,
            string.IsNullOrWhiteSpace(argsText) ? Loc.T("traj.no_payload") : argsText,
            string.IsNullOrWhiteSpace(err) ? resultText : err + "\n\n" + resultText,
            schemaText,
            timing,
            toolName: name,
            callId: callId,
            weight: durationMs is { } ms ? Math.Max(1, ms) : 800);
    }

    private static IReadOnlyDictionary<string, string> IndexToolCatalog(IReadOnlyList<SessionEntry> entries)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase)
                || entry.Payload is not JsonElement payload
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("kind", out var kind)
                || !string.Equals(kind.GetString(), "tool_catalog", StringComparison.OrdinalIgnoreCase)
                || !payload.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object
                    || !tool.TryGetProperty("name", out var nameEl))
                {
                    continue;
                }

                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                map[name] = PrettyJson(tool);
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string>? MergeHints(
        IReadOnlyDictionary<string, string>? live,
        IReadOnlyDictionary<string, string> catalog)
    {
        if (catalog.Count == 0)
        {
            return live;
        }

        var merged = new Dictionary<string, string>(catalog, StringComparer.OrdinalIgnoreCase);
        if (live is not null)
        {
            foreach (var kv in live)
            {
                merged[kv.Key] = kv.Value;
            }
        }

        return merged;
    }

    private static Dictionary<string, SessionEntry> IndexAgentInvocations(IReadOnlyList<SessionEntry> entries)
    {
        var map = new Dictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(SessionPayloadJson.ReadString(entry, "kind"), "tool_invocation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var callId = SessionPayloadJson.ReadString(entry, "callId");
            if (!string.IsNullOrWhiteSpace(callId))
            {
                map[callId] = entry;
            }
        }

        return map;
    }

    private static string FormatNamedJson(SessionEntry entry, string property)
    {
        if (entry.Payload is not JsonElement payload
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(property, out var el)
            || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "";
        }

        return PrettyJson(el);
    }

    private static TrajectoryRecordVm OrphanToolResult(
        SessionEntry entry,
        Dictionary<string, string> schemaByCall)
    {
        var callId = SessionPayloadJson.ReadString(entry, "callId") ?? entry.Id;
        var resultText = FormatToolResult(entry);
        var durationMs = ReadLong(entry, "durationMs");
        var started = ReadTime(entry, "startedAt");
        var ended = ReadTime(entry, "endedAt") ?? entry.Timestamp;
        schemaByCall.TryGetValue(callId, out var schema);

        return Make(
            entry.Id,
            TrajectoryKind.Tool,
            Loc.T("traj.kind.tool"),
            "result",
            Truncate(SummarizeResult(resultText), 100),
            started ?? entry.Timestamp,
            ended,
            durationMs,
            Loc.T("traj.level.tool_result"),
            Loc.T("traj.status.completed"),
            Loc.T("traj.no_payload"),
            resultText,
            schema ?? Loc.T("traj.schema_na"),
            FormatTiming(started ?? entry.Timestamp, ended, durationMs),
            callId: callId,
            weight: durationMs ?? 400);
    }

    private static TrajectoryRecordVm ApprovalRecord(SessionEntry entry)
    {
        var status = SessionPayloadJson.ReadString(entry, "status") ?? "pending";
        var text = SessionPayloadJson.ReadString(entry, "text") ?? SessionPayloadJson.ReadString(entry, "toolName") ?? "approval";
        var tool = SessionPayloadJson.ReadString(entry, "toolName") ?? SessionPayloadJson.ReadString(entry, "suggestedAction");
        return Make(
            entry.Id,
            TrajectoryKind.Approval,
            Loc.T("traj.kind.approval"),
            status,
            Truncate(text, 100),
            entry.Timestamp,
            null,
            null,
            Loc.T("traj.level.approval"),
            status,
            PayloadPretty(entry) ?? Loc.T("traj.no_payload"),
            "",
            ResolveSchema(tool, null),
            FormatTiming(entry.Timestamp, null, null),
            toolName: tool,
            weight: 200);
    }

    private static TrajectoryRecordVm ErrorRecord(SessionEntry entry)
    {
        var msg = SessionPayloadJson.ReadString(entry, "message") ?? SessionPayloadJson.ReadString(entry, "text") ?? "error";
        return Make(
            entry.Id,
            TrajectoryKind.Error,
            Loc.T("traj.kind.error"),
            Loc.T("traj.kind.error"),
            Truncate(msg, 100),
            entry.Timestamp,
            null,
            null,
            Loc.T("traj.level.error"),
            Loc.T("traj.status.failed"),
            msg,
            "",
            Loc.T("traj.schema_na"),
            FormatTiming(entry.Timestamp, null, null),
            weight: 300);
    }

    private static TrajectoryRecordVm MetaRecord(
        SessionEntry entry,
        string kind,
        string summary,
        DateTimeOffset? endedAt,
        DateTimeOffset? startedOverride)
    {
        var start = startedOverride ?? entry.Timestamp;
        var end = endedAt;
        long? ms = end is { } e ? (long)Math.Max(0, (e - start).TotalMilliseconds) : null;
        return Make(
            entry.Id,
            TrajectoryKind.Meta,
            Loc.T("traj.kind.meta"),
            kind,
            summary,
            start,
            end,
            ms,
            Loc.T("traj.level.meta", kind),
            Loc.T("traj.status.recorded"),
            PayloadPretty(entry) ?? Loc.T("traj.no_payload"),
            "",
            Loc.T("traj.schema_na"),
            FormatTiming(start, end, ms),
            weight: ms ?? 100);
    }

    private static TrajectoryRecordVm TurnTimingRecord(SessionEntry entry)
    {
        var start = ReadTime(entry, "startedAt") ?? entry.Timestamp;
        var end = ReadTime(entry, "endedAt");
        var durationMs = ReadLong(entry, "durationMs");
        var ttft = ReadLong(entry, "ttftMs");
        var tools = ReadLong(entry, "toolCalls");
        var subtitle = Loc.T(
            "traj.meta.turn_timing",
            durationMs?.ToString(CultureInfo.InvariantCulture) ?? "?",
            ttft?.ToString(CultureInfo.InvariantCulture) ?? "—",
            tools?.ToString(CultureInfo.InvariantCulture) ?? "0");

        var timing = FormatTiming(start, end, durationMs);
        if (ttft is not null)
        {
            timing += "\n" + Loc.T("traj.timing.ttft", $"{ttft}ms");
        }

        if (tools is not null)
        {
            timing += "\n" + Loc.T("traj.timing.tool_calls", tools.Value.ToString(CultureInfo.InvariantCulture));
        }

        return Make(
            entry.Id,
            TrajectoryKind.Meta,
            Loc.T("traj.kind.meta"),
            Loc.T("traj.level.timing"),
            subtitle,
            start,
            end,
            durationMs,
            Loc.T("traj.level.timing"),
            Loc.T("traj.status.recorded"),
            PayloadPretty(entry) ?? Loc.T("traj.no_payload"),
            "",
            Loc.T("traj.schema_na"),
            timing,
            weight: durationMs ?? 200);
    }

    private static TrajectoryRecordVm UsageRecord(SessionEntry entry)
    {
        var input = ReadNumber(entry, "inputTokens");
        var output = ReadNumber(entry, "outputTokens");
        var total = ReadNumber(entry, "totalTokens");
        var summary = Loc.T(
            "traj.meta.usage",
            input?.ToString(CultureInfo.InvariantCulture) ?? "?",
            output?.ToString(CultureInfo.InvariantCulture) ?? "?",
            total?.ToString(CultureInfo.InvariantCulture) ?? "?");

        return Make(
            entry.Id,
            TrajectoryKind.Meta,
            Loc.T("traj.kind.meta"),
            Loc.T("traj.level.usage"),
            summary,
            entry.Timestamp,
            null,
            null,
            Loc.T("traj.level.usage"),
            Loc.T("traj.status.recorded"),
            PayloadPretty(entry) ?? Loc.T("traj.no_payload"),
            "",
            Loc.T("traj.schema_na"),
            FormatTiming(entry.Timestamp, null, null)
            + "\n"
            + Loc.T(
                "traj.timing.tokens",
                input?.ToString(CultureInfo.InvariantCulture) ?? "—",
                output?.ToString(CultureInfo.InvariantCulture) ?? "—",
                total?.ToString(CultureInfo.InvariantCulture) ?? "—"),
            weight: 50);
    }

    private static TrajectoryRecordVm Make(
        string id,
        TrajectoryKind kind,
        string kindLabel,
        string title,
        string subtitle,
        DateTimeOffset started,
        DateTimeOffset? ended,
        long? durationMs,
        string overviewLevel,
        string overviewStatus,
        string parameters,
        string result,
        string schema,
        string timing,
        string? toolName = null,
        string? callId = null,
        double weight = 1)
    {
        var (fg, soft) = KindColors[kind];
        var summary = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}  {subtitle}";
        return new TrajectoryRecordVm
        {
            RecordId = id,
            Kind = kind,
            KindLabel = kindLabel,
            Title = title,
            Subtitle = subtitle,
            Summary = Truncate(summary, 160),
            StartedAt = started,
            EndedAt = ended,
            DurationMs = durationMs,
            ToolName = toolName,
            CallId = callId,
            OverviewLevel = overviewLevel,
            OverviewStatus = overviewStatus,
            ParametersText = parameters,
            ResultText = result,
            SchemaText = schema,
            TimingText = timing,
            ColorHex = fg,
            SoftColorHex = soft,
            TimelineWeight = weight
        };
    }

    private static string? FormatSchemaFromCall(SessionEntry call)
    {
        if (call.Payload is not JsonElement payload
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("schema", out var schema)
            || schema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return PrettyJson(schema);
    }

    private static string ResolveSchema(string? toolName, IReadOnlyDictionary<string, string>? hints)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Loc.T("traj.schema_na");
        }

        if (hints is not null
            && hints.TryGetValue(toolName, out var desc)
            && !string.IsNullOrWhiteSpace(desc))
        {
            // Catalog stores full tool JSON; live /tools hints are short descriptions.
            var trimmed = desc.TrimStart();
            return trimmed.StartsWith('{') || trimmed.StartsWith('[')
                ? desc
                : $"{toolName}\n\n{desc}";
        }

        return Loc.T("traj.schema_unavailable", toolName);
    }

    /// <summary>
    /// Meta kinds that only enrich other rows (or are chat noise). Everything else is shown —
    /// including unknown future kinds via <c>traj.meta.generic</c>.
    /// </summary>
    private static bool IsTrajectoryNoiseMeta(string kind) =>
        kind is "tool_catalog" or "tool_invocation" or "approval_queue" or "approval_promoted"
            or "session_created" or "attachment_uploaded";

    private static bool SameCallId(SessionEntry call, SessionEntry result)
    {
        var a = SessionPayloadJson.ReadString(call, "callId");
        var b = SessionPayloadJson.ReadString(result, "callId");
        return !string.IsNullOrWhiteSpace(a)
               && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatArguments(SessionEntry call)
    {
        if (call.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (!payload.TryGetProperty("arguments", out var args))
        {
            return PayloadPretty(call) ?? "";
        }

        return PrettyJson(args);
    }

    private static string FormatToolResult(SessionEntry result)
    {
        if (result.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return PayloadPretty(result) ?? Loc.T("traj.no_result");
        }

        if (payload.TryGetProperty("result", out var r))
        {
            if (r.ValueKind == JsonValueKind.String)
            {
                var s = r.GetString() ?? "";
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    return PrettyJson(doc.RootElement);
                }
                catch
                {
                    return s;
                }
            }

            return PrettyJson(r);
        }

        return PrettyJson(payload);
    }

    private static string SummarizeResult(string resultText) => Truncate(Collapse(resultText), 48);

    private static string FormatTiming(DateTimeOffset start, DateTimeOffset? end, long? durationMs)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("traj.timing.start", start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")));
        if (end is { } e)
        {
            sb.AppendLine(Loc.T("traj.timing.end", e.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")));
        }

        var ms = durationMs ?? (end is { } e2 ? (long?)Math.Max(0, (e2 - start).TotalMilliseconds) : null);
        if (ms is { } d)
        {
            sb.Append(Loc.T(
                "traj.timing.duration",
                d >= 1000 ? $"{d / 1000.0:0.##}s" : $"{d}ms"));
        }
        else
        {
            sb.Append(Loc.T("traj.timing.no_duration"));
        }

        return sb.ToString().TrimEnd();
    }

    private static string? ExtractText(SessionEntry entry)
    {
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "text", "content", "message" })
        {
            if (payload.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }

        return null;
    }


    private static DateTimeOffset? ReadTime(SessionEntry entry, string name)
    {
        var s = SessionPayloadJson.ReadString(entry, name);
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : null;
    }

    private static long? ReadLong(SessionEntry entry, string name)
    {
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => n,
            _ => null
        };
    }

    private static double? ReadNumber(SessionEntry entry, string name)
    {
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    private static string? PayloadPretty(SessionEntry entry) =>
        entry.Payload is JsonElement el ? PrettyJson(el) : null;

    private static string PrettyJson(JsonElement el)
    {
        try
        {
            return JsonSerializer.Serialize(el, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return el.GetRawText();
        }
    }

    private static string Collapse(string text) =>
        text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static double EstimateWeightFromText(string text) =>
        Math.Clamp(text.Length / 4.0, 50, 5000);
}
