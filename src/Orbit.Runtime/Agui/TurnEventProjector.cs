using System.Collections;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Orbit.Contracts;
using Orbit.Runtime.Sessions;

namespace Orbit.Runtime.Agui;

/// <summary>Map AG-UI stream contents into session JSONL events.</summary>
public sealed class TurnEventProjector(ISessionStore sessions)
{
    public void FlushThinking(string sessionId, StringBuilder thinkingText)
    {
        if (thinkingText.Length == 0)
        {
            return;
        }

        sessions.Append(sessionId, SessionEvents.Thinking(thinkingText.ToString()));
        thinkingText.Clear();
    }

    public void AppendUsage(string sessionId, UsageDetails? details)
    {
        if (details is null)
        {
            return;
        }

        sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.Usage, new
        {
            source = "provider",
            inputTokens = details.InputTokenCount,
            outputTokens = details.OutputTokenCount,
            totalTokens = details.TotalTokenCount
        }));
    }

    public void AppendToolApproval(string sessionId, ToolApprovalRequestContent approval)
    {
        var call = approval.ToolCall as FunctionCallContent;
        var toolName = call?.Name ?? "(tool)";
        var callId = approval.ToolCall.CallId ?? "";
        var args = call?.Arguments;
        var argPreview = args is { Count: > 0 }
            ? string.Join(", ", args.Select(kv => $"{kv.Key}={TruncateArg(kv.Value)}"))
            : "";

        var prompt = ReadArgString(args, "prompt");
        var title = ReadArgString(args, "title");
        var options = ReadArgStringList(args, "options");
        var isChoice = OrbitAskTools.IsChoice(toolName);
        var isConfirm = OrbitAskTools.IsConfirm(toolName);

        // Oversized / invalid choice → plain tool card (no option buttons); tool body returns an error.
        string cardKind;
        string? invalidChoiceHint = null;
        if (isChoice)
        {
            if (OrbitAskTools.TryNormalizeChoice(prompt, options, out var normPrompt, out var normOptions, out var choiceError))
            {
                cardKind = "choice";
                prompt = normPrompt;
                options = normOptions;
            }
            else
            {
                cardKind = "tool";
                options = null;
                invalidChoiceHint = string.IsNullOrWhiteSpace(choiceError)
                    ? "Invalid choice options."
                    : choiceError;
            }
        }
        else if (isConfirm)
        {
            cardKind = "confirm";
            if (!string.IsNullOrWhiteSpace(prompt) && prompt.Length > OrbitAskTools.MaxPromptLength)
            {
                prompt = prompt[..OrbitAskTools.MaxPromptLength];
            }
        }
        else
        {
            cardKind = "tool";
        }

        var text = !string.IsNullOrWhiteSpace(prompt)
            ? prompt!
            : !string.IsNullOrWhiteSpace(title)
                ? title!
                : string.IsNullOrWhiteSpace(argPreview)
                    ? $"Run tool \"{toolName}\""
                    : $"Run tool \"{toolName}\" ({argPreview})";
        if (!string.IsNullOrWhiteSpace(invalidChoiceHint))
        {
            text = $"{text}\n\n(Invalid options: {invalidChoiceHint})";
        }

        var detail = sessions.Get(sessionId);
        var hasActivePending = detail?.Entries.Any(IsPendingApproval) == true;

        sessions.Append(sessionId, SessionEvents.Approval(new
        {
            status = hasActivePending ? ApprovalStatuses.Queued : ApprovalStatuses.Pending,
            source = ApprovalSources.Tool,
            id = approval.RequestId,
            interruptId = approval.RequestId,
            callId,
            toolName,
            text,
            title,
            cardKind,
            options,
            suggestedAction = toolName,
            arguments = AguiHistorySanitizer.ToStorageArgs(args),
            requiresApproval = true
        }));
    }

    private static string? ReadArgString(IDictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => value.ToString()
        };
    }

    private static IReadOnlyList<string>? ReadArgStringList(IDictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                return el.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? x.GetRawText() : x.GetRawText())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                return [el.GetString() ?? ""];
            }
        }

        if (value is IEnumerable<string> strings)
        {
            return strings.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        if (value is IEnumerable enumerable)
        {
            var list = new List<string>();
            foreach (var item in enumerable)
            {
                var s = item?.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    list.Add(s);
                }
            }

            return list.Count == 0 ? null : list;
        }

        return null;
    }

    public static bool TryReadToolApproval(
        SessionEntry entry,
        out string requestId,
        out string callId,
        out string toolName,
        out IDictionary<string, object?>? args)
    {
        requestId = "";
        callId = "";
        toolName = "";
        args = null;

        if (!string.Equals(entry.Type, "approval", StringComparison.OrdinalIgnoreCase)
            || entry.Payload is not JsonElement payload
            || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!ApprovalSources.IsToolInterrupt(SessionPayloadJson.ReadString(payload, "source")))
        {
            return false;
        }

        requestId = SessionPayloadJson.ReadString(payload, "interruptId") ?? "";
        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId = SessionPayloadJson.ReadString(payload, "id") ?? "";
        }

        callId = SessionPayloadJson.ReadString(payload, "callId") ?? "";
        toolName = SessionPayloadJson.ReadString(payload, "toolName") ?? "";
        if (string.IsNullOrWhiteSpace(toolName))
        {
            toolName = SessionPayloadJson.ReadString(payload, "suggestedAction") ?? "";
        }

        if (payload.TryGetProperty("arguments", out var a) && a.ValueKind is JsonValueKind.Object)
        {
            args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in a.EnumerateObject())
            {
                args[prop.Name] = AguiArgumentNormalizer.Normalize(prop.Value);
            }
        }

        return !string.IsNullOrWhiteSpace(requestId) && !string.IsNullOrWhiteSpace(toolName);
    }

    private static bool IsPendingApproval(SessionEntry entry)
    {
        if (!string.Equals(entry.Type, "approval", StringComparison.OrdinalIgnoreCase)
            || entry.Payload is not JsonElement payload
            || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return string.Equals(
            SessionPayloadJson.ReadString(payload, "status"),
            ApprovalStatuses.Pending,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateArg(object? value)
    {
        var s = value?.ToString() ?? "";
        return s.Length <= 48 ? s : s[..45] + "…";
    }
}
