using System.Text.Json;
using Orbit.Contracts;
using Orbit.Shell.I18n;

namespace Orbit.Shell.ViewModels;

/// <summary>Projects raw session entries into Cursor-like chat rows (thought / tools / todos / reply).</summary>
public static class ConversationProjector
{
    public static IReadOnlyList<SessionEntryVm> Project(IReadOnlyList<SessionEntry> entries)
    {
        var queuedCount = entries.Count(e => ReadApprovalStatus(e) == "queued");
        var queueHintEmitted = false;
        var result = new List<SessionEntryVm>();
        var todos = new List<TodoItemVm>();
        var nextTodoId = 1;

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (IsNoiseMeta(entry))
            {
                continue;
            }

            if (string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase)
                && string.Equals(SessionPayloadJson.ReadString(entry, "kind"), "run_interrupted", StringComparison.OrdinalIgnoreCase))
            {
                var reason = SessionPayloadJson.ReadString(entry, "reason") ?? "interrupted";
                result.Add(SessionEntryVm.CreateChip(
                    entry.Id,
                    entry.Timestamp,
                    label: Loc.T("chat.run_interrupted", reason),
                    detail: ""));
                continue;
            }

            if (string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase)
                && string.Equals(SessionPayloadJson.ReadString(entry, "kind"), SessionMetaKinds.ResearchStage, StringComparison.OrdinalIgnoreCase))
            {
                var stage = SessionPayloadJson.ReadString(entry, "stage") ?? "step";
                result.Add(SessionEntryVm.CreateChip(
                    entry.Id,
                    entry.Timestamp,
                    label: Loc.T("chat.research_stage", stage),
                    detail: ""));
                continue;
            }

            if (string.Equals(entry.Type, "approval", StringComparison.OrdinalIgnoreCase))
            {
                var status = ReadApprovalStatus(entry);
                if (status == "queued")
                {
                    continue;
                }

                if (status is "approved" or "rejected")
                {
                    var label = status == "approved" ? Loc.T("chat.approved") : Loc.T("chat.rejected");
                    var text = SessionPayloadJson.ReadString(entry, "text");
                    result.Add(SessionEntryVm.CreateChip(
                        entry.Id,
                        entry.Timestamp,
                        label: string.IsNullOrWhiteSpace(text) ? label : $"{label} · {Truncate(text!, 42)}",
                        detail: ""));
                    continue;
                }

                result.Add(SessionEntryVm.From(entry));
                if (!queueHintEmitted && queuedCount > 0)
                {
                    result.Add(SessionEntryVm.CreateChip(
                        "approval-queue-hint",
                        entry.Timestamp,
                        label: Loc.T("chat.queue_hint", queuedCount),
                        detail: ""));
                    queueHintEmitted = true;
                }

                continue;
            }

            if (string.Equals(entry.Type, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                var batchEntries = new List<SessionEntry> { entry };
                var batch = new List<SessionEntryVm> { SessionEntryVm.From(entry) };
                while (i + 1 < entries.Count
                       && string.Equals(entries[i + 1].Type, "thinking", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    batchEntries.Add(entries[i]);
                    batch.Add(SessionEntryVm.From(entries[i]));
                }

                var duration = batchEntries[^1].Timestamp - batchEntries[0].Timestamp;
                // Include gap until next entry when a single thought blob is followed by tools.
                if (batchEntries.Count == 1 && i + 1 < entries.Count)
                {
                    duration = entries[i + 1].Timestamp - batchEntries[0].Timestamp;
                }

                result.Add(SessionEntryVm.MergeThoughts(batch, duration));
                continue;
            }

            if (string.Equals(entry.Type, "tool_call", StringComparison.OrdinalIgnoreCase))
            {
                var name = SessionPayloadJson.ReadString(entry, "name") ?? "tool";
                SessionEntry? resultEntry = null;
                if (i + 1 < entries.Count
                    && string.Equals(entries[i + 1].Type, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    resultEntry = entries[i + 1];
                    i++;
                }

                var status = resultEntry is null ? "…" : SummarizeToolResult(resultEntry);
                var argsText = FormatArgs(entry);
                var resultText = FormatResult(resultEntry);

                if (IsTodoTool(name))
                {
                    ApplyTodoMutation(todos, ref nextTodoId, name, entry);
                    if (todos.Count > 0)
                    {
                        result.Add(SessionEntryVm.CreateTodoCard(
                            entry.Id,
                            entry.Timestamp,
                            todos.Select(t => new TodoItemVm
                            {
                                Id = t.Id,
                                Title = t.Title,
                                Done = t.Done,
                                Reason = t.Reason
                            }).ToList()));
                    }

                    continue;
                }

                var row = ToolActivityFormatter.Format(name, status, argsText, resultText);
                result.Add(SessionEntryVm.CreateActivityLine(entry.Id, entry.Timestamp, row));
                continue;
            }

            if (string.Equals(entry.Type, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(SessionEntryVm.From(entry));
        }

        return result;
    }

    private static bool IsTodoTool(string name) =>
        name.StartsWith("todos_", StringComparison.OrdinalIgnoreCase);

    private static void ApplyTodoMutation(
        List<TodoItemVm> todos,
        ref int nextTodoId,
        string toolName,
        SessionEntry call)
    {
        var args = ReadArgsObject(call);
        if (string.Equals(toolName, "todos_add", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in ParseTodoAddList(args))
            {
                todos.Add(new TodoItemVm
                {
                    Id = nextTodoId++,
                    Title = item,
                    Done = false
                });
            }

            return;
        }

        if (string.Equals(toolName, "todos_complete", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "todos_update", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (id, reason, done) in ParseTodoCompleteItems(args, toolName))
            {
                var match = todos.FirstOrDefault(t => t.Id == id);
                if (match is null)
                {
                    continue;
                }

                var idx = todos.IndexOf(match);
                todos[idx] = new TodoItemVm
                {
                    Id = match.Id,
                    Title = match.Title,
                    Done = done,
                    Reason = reason ?? match.Reason
                };
            }
        }
    }

    private static IEnumerable<string> ParseTodoAddList(JsonElement? args)
    {
        if (args is null || args.Value.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (!args.Value.TryGetProperty("todos", out var todosEl))
        {
            yield break;
        }

        foreach (var el in EnumerateJsonArrayish(todosEl))
        {
            if (el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String)
            {
                var t = title.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    yield return t!;
                }
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                var t = el.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    yield return t!;
                }
            }
        }
    }

    private static IEnumerable<(int Id, string? Reason, bool Done)> ParseTodoCompleteItems(
        JsonElement? args,
        string toolName)
    {
        if (args is null || args.Value.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (!args.Value.TryGetProperty("items", out var itemsEl)
            && !args.Value.TryGetProperty("todos", out itemsEl))
        {
            yield break;
        }

        var isCompleteTool = string.Equals(toolName, "todos_complete", StringComparison.OrdinalIgnoreCase);
        foreach (var el in EnumerateJsonArrayish(itemsEl))
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = 0;
            if (el.TryGetProperty("id", out var idEl))
            {
                if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt32(out var n))
                {
                    id = n;
                }
                else if (idEl.ValueKind == JsonValueKind.String && int.TryParse(idEl.GetString(), out var p))
                {
                    id = p;
                }
            }

            if (id <= 0)
            {
                continue;
            }

            string? reason = null;
            if (el.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                reason = r.GetString();
            }

            var done = isCompleteTool;
            if (el.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
            {
                var s = st.GetString()?.ToLowerInvariant();
                done = s is "completed" or "done" or "complete";
            }
            else if (el.TryGetProperty("completed", out var c)
                     && c.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                done = c.GetBoolean();
            }

            yield return (id, reason, done);
        }
    }

    private static IEnumerable<JsonElement> EnumerateJsonArrayish(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            return el.EnumerateArray().Select(x => x.Clone()).ToList();
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var raw = el.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static JsonElement? ReadArgsObject(SessionEntry entry)
    {
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!payload.TryGetProperty("arguments", out var args))
        {
            return null;
        }

        if (args.ValueKind == JsonValueKind.Object)
        {
            return args;
        }

        if (args.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(args.GetString() ?? "{}");
                return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string FormatArgs(SessionEntry call)
    {
        if (call.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (!payload.TryGetProperty("arguments", out var args) || args.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "";
        }

        var raw = args.ValueKind == JsonValueKind.String ? args.GetString() ?? "" : args.GetRawText();
        return Compact(raw, 1200);
    }

    private static string FormatResult(SessionEntry? result)
    {
        if (result?.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        if (!payload.TryGetProperty("result", out var r))
        {
            return "";
        }

        var raw = r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : r.GetRawText();
        return Compact(raw, 2000);
    }

    private static string Compact(string raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        raw = raw.Replace("\r\n", "\n").Trim();
        return raw.Length <= max ? raw : raw[..max] + "…";
    }

    private static bool IsNoiseMeta(SessionEntry entry)
    {
        if (!string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var kind = SessionPayloadJson.ReadString(entry, "kind");
        return kind is "agui_run_started" or "agui_run_finished" or "approval_queue" or "approval_promoted";
    }

    private static string? ReadApprovalStatus(SessionEntry entry)
    {
        if (!string.Equals(entry.Type, "approval", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return SessionPayloadJson.ReadString(entry, "status")?.ToLowerInvariant();
    }

    private static string SummarizeToolResult(SessionEntry entry)
    {
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return "ok";
        }

        if (payload.TryGetProperty("result", out var resultEl))
        {
            var raw = resultEl.ValueKind == JsonValueKind.String
                ? resultEl.GetString()
                : resultEl.GetRawText();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                        {
                            return "failed";
                        }

                        if (doc.RootElement.TryGetProperty("count", out var count)
                            && count.TryGetInt32(out var n))
                        {
                            return $"{n} items";
                        }

                        if (doc.RootElement.TryGetProperty("action", out var action)
                            && action.ValueKind == JsonValueKind.String)
                        {
                            return action.GetString() ?? "ok";
                        }

                        if (doc.RootElement.TryGetProperty("hint", out var hint)
                            && hint.ValueKind == JsonValueKind.String)
                        {
                            var h = hint.GetString() ?? "";
                            return h.Length <= 36 ? h : h[..36] + "…";
                        }
                    }
                }
                catch
                {
                    // fall through
                }

                return "ok";
            }
        }

        return "ok";
    }


    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
