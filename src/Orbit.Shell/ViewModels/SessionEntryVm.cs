using System.Text.Json;
using Orbit.Contracts;
using Orbit.Shell.I18n;

namespace Orbit.Shell.ViewModels;

public sealed class SessionEntryVm
{
    public required string EntryId { get; init; }
    public required string Type { get; init; }
    public required string Timestamp { get; init; }
    public required string PayloadPreview { get; init; }
    public required string DisplayBody { get; init; }
    public required string HeaderText { get; init; }
    public required string SummaryLabel { get; init; }
    public bool IsPendingApproval { get; init; }
    public bool IsChoiceCard { get; init; }
    public bool IsConfirmCard { get; init; }
    public bool IsFailedApproval { get; init; }
    /// <summary>Decision in flight (status approving or local optimistic lock).</summary>
    public bool IsApproving { get; init; }
    /// <summary>Show Approve/Reject/Retry / choice options (hidden while approving).</summary>
    public bool ShowDecisionButtons { get; init; }
    public string ApprovalStatusLabel { get; init; } = "";
    public string ApproveLabel { get; init; } = "";
    public string RejectLabel { get; init; } = "";
    public string RetryLabel { get; init; } = "";
    public string? FailMessage { get; init; }
    public bool IsUser { get; init; }
    public bool IsAssistant { get; init; }
    public bool IsThought { get; init; }
    public bool IsToolChip { get; init; }
    public bool IsToolGroup { get; init; }
    public bool IsTodoCard { get; init; }
    public bool IsMetaQuiet { get; init; }
    public bool IsCollapsible { get; init; }
    public string? SuggestedAction { get; init; }
    public string? ApprovalText { get; init; }
    public string? CardTitle { get; init; }
    public bool HasCardTitle => !string.IsNullOrWhiteSpace(CardTitle);
    public string? Status { get; init; }
    public bool IsExpanded { get; set; }
    public string CopyLabel { get; init; } = "Copy";
    public IReadOnlyList<ApprovalChoiceOptionVm> ChoiceOptions { get; init; } = [];
    public IReadOnlyList<ToolCallRowVm> ToolRows { get; init; } = [];
    public IReadOnlyList<TodoItemVm> TodoItems { get; init; } = [];
    public int TodoDoneCount => TodoItems.Count(t => t.Done);
    public int TodoTotalCount => TodoItems.Count;
    public string TodoProgressLabel => Loc.T("chat.todo_progress", TodoDoneCount, TodoTotalCount);

    public static SessionEntryVm CreateChip(string id, DateTimeOffset timestamp, string label, string detail) =>
        new()
        {
            EntryId = id,
            Type = "tool_chip",
            Timestamp = timestamp.ToLocalTime().ToString("HH:mm"),
            PayloadPreview = detail,
            DisplayBody = label,
            HeaderText = label,
            SummaryLabel = label,
            IsToolChip = true,
            IsCollapsible = false,
            IsExpanded = false
        };

    /// <summary>Cursor-style activity block: one muted line per tool, no nested expanders.</summary>
    public static SessionEntryVm CreateToolGroup(
        string id,
        DateTimeOffset timestamp,
        IReadOnlyList<ToolCallRowVm> rows) =>
        new()
        {
            EntryId = id,
            Type = "tool_group",
            Timestamp = timestamp.ToLocalTime().ToString("HH:mm"),
            PayloadPreview = "",
            DisplayBody = "",
            HeaderText = Loc.T("chat.tools_n", rows.Count),
            SummaryLabel = Loc.T("chat.tools_n", rows.Count),
            IsToolGroup = true,
            IsCollapsible = false,
            IsExpanded = false,
            ToolRows = rows
        };

    public static SessionEntryVm CreateActivityLine(string id, DateTimeOffset timestamp, ToolCallRowVm row) =>
        new()
        {
            EntryId = id,
            Type = "tool_line",
            Timestamp = timestamp.ToLocalTime().ToString("HH:mm"),
            PayloadPreview = row.ArgsText,
            DisplayBody = row.Line,
            HeaderText = row.Line,
            SummaryLabel = row.Line,
            IsToolChip = true,
            IsCollapsible = false,
            IsExpanded = false,
            ToolRows = [row]
        };

    public static SessionEntryVm CreateTodoCard(
        string id,
        DateTimeOffset timestamp,
        IReadOnlyList<TodoItemVm> items) =>
        new()
        {
            EntryId = id,
            Type = "todo_card",
            Timestamp = timestamp.ToLocalTime().ToString("HH:mm"),
            PayloadPreview = "",
            DisplayBody = "",
            HeaderText = Loc.T("chat.todo"),
            SummaryLabel = Loc.T("chat.todo"),
            IsTodoCard = true,
            IsCollapsible = true,
            IsExpanded = true,
            TodoItems = items
        };

    public static SessionEntryVm CreateOptimisticUser(string text) =>
        new()
        {
            EntryId = "optimistic-user",
            Type = "user",
            Timestamp = DateTimeOffset.Now.ToString("HH:mm"),
            PayloadPreview = text,
            DisplayBody = text,
            HeaderText = text,
            SummaryLabel = "user",
            IsUser = true,
            CopyLabel = Loc.T("app.copy")
        };

    public static SessionEntryVm From(SessionEntry entry)
    {
        string? status = null;
        string? suggestedAction = null;
        string? targetFolder = null;
        string? approvalText = null;
        string? approvalReason = null;
        string? failMessage = null;
        string? text = null;
        string? metaKind = null;
        string? cardKind = null;
        string? toolName = null;
        string? cardTitle = null;
        var choiceLabels = new List<string>();

        if (entry.Payload is JsonElement payload && payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("status", out var s))
            {
                status = s.GetString();
            }

            if (payload.TryGetProperty("suggestedAction", out var a))
            {
                suggestedAction = a.GetString();
            }

            if (payload.TryGetProperty("targetFolder", out var tf) && tf.ValueKind == JsonValueKind.String)
            {
                targetFolder = tf.GetString();
            }

            if (payload.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                approvalReason = r.GetString();
            }

            if (payload.TryGetProperty("failMessage", out var fm) && fm.ValueKind == JsonValueKind.String)
            {
                failMessage = fm.GetString();
            }

            if (payload.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                approvalText = t.GetString();
            }

            if (payload.TryGetProperty("cardKind", out var ck) && ck.ValueKind == JsonValueKind.String)
            {
                cardKind = ck.GetString();
            }

            if (payload.TryGetProperty("toolName", out var tn) && tn.ValueKind == JsonValueKind.String)
            {
                toolName = tn.GetString();
            }

            if (payload.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
            {
                cardTitle = titleEl.GetString();
            }

            if (payload.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in optEl.EnumerateArray())
                {
                    var label = item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText();
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        choiceLabels.Add(label!);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(approvalText))
            {
                var from = payload.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString()
                    : null;
                var subject = payload.TryGetProperty("subject", out var sub) && sub.ValueKind == JsonValueKind.String
                    ? sub.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(subject))
                {
                    approvalText = string.IsNullOrWhiteSpace(from) ? subject
                        : string.IsNullOrWhiteSpace(subject) ? from
                        : $"{from} — {subject}";
                }
            }

            text = approvalText;

            if (text is null && payload.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                text = c.GetString();
            }

            if (text is null && payload.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                text = m.GetString();
            }

            if (payload.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
            {
                metaKind = k.GetString();
            }
        }

        var actionLabel = FormatActionLabel(suggestedAction, targetFolder);

        var isApproval = string.Equals(entry.Type, "approval", StringComparison.OrdinalIgnoreCase);
        var pending = isApproval && string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);
        var approving = isApproval && string.Equals(status, "approving", StringComparison.OrdinalIgnoreCase);
        var failed = isApproval && string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
        var interactive = pending || approving;

        var resolvedKind = cardKind
            ?? (OrbitAskTools.IsChoice(toolName) ? "choice"
                : OrbitAskTools.IsConfirm(toolName) ? "confirm"
                : "tool");
        var isChoice = interactive
                       && string.Equals(resolvedKind, "choice", StringComparison.OrdinalIgnoreCase)
                       && choiceLabels.Count > 0;
        var isConfirm = interactive
                        && string.Equals(resolvedKind, "confirm", StringComparison.OrdinalIgnoreCase);
        var isToolPending = interactive && !isChoice && !isConfirm;

        var preview = entry.Payload is JsonElement p ? CompactJson(p) : "";
        var isUser = string.Equals(entry.Type, "user", StringComparison.OrdinalIgnoreCase);
        var isAssistant = string.Equals(entry.Type, "assistant", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(entry.Type, "error", StringComparison.OrdinalIgnoreCase);
        var isThought = string.Equals(entry.Type, "thinking", StringComparison.OrdinalIgnoreCase);
        var isMeta = string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase);

        var body = !string.IsNullOrWhiteSpace(text)
            ? text!
            : isUser || isAssistant || isThought
                ? ExtractReadable(preview)
                : preview;

        var summaryLabel = isChoice
            ? Loc.T("chat.choice")
            : isConfirm
                ? Loc.T("chat.confirm")
                : approving
                    ? Loc.T("chat.approving")
                    : pending
                        ? Loc.T("chat.pending")
                        : failed
                            ? Loc.T("chat.failed")
                            : isThought
                                ? Loc.T("chat.thought")
                                : isMeta
                                    ? (metaKind ?? "meta")
                                    : entry.Type;

        var choices = isChoice
            ? choiceLabels.Select(label => new ApprovalChoiceOptionVm(entry.Id, label)).ToList()
            : [];

        var statusLabel = approving
            ? Loc.T("chat.approving")
            : failed
                ? Loc.T("chat.failed_retry")
                : isChoice
                    ? Loc.T("chat.choice")
                    : isConfirm
                        ? Loc.T("chat.confirm")
                        : Loc.T("chat.pending");

        return new SessionEntryVm
        {
            EntryId = entry.Id,
            Type = entry.Type,
            Timestamp = entry.Timestamp.ToLocalTime().ToString("HH:mm"),
            PayloadPreview = preview,
            DisplayBody = body,
            SummaryLabel = summaryLabel,
            IsPendingApproval = isToolPending,
            IsChoiceCard = isChoice,
            IsConfirmCard = isConfirm,
            IsFailedApproval = failed,
            IsApproving = approving,
            ShowDecisionButtons = (pending || failed) && !approving,
            ApprovalStatusLabel = statusLabel,
            ApproveLabel = Loc.T("app.approve"),
            RejectLabel = Loc.T("app.reject"),
            RetryLabel = Loc.T("app.retry"),
            FailMessage = failMessage,
            IsUser = isUser,
            IsAssistant = isAssistant,
            IsThought = isThought,
            IsToolChip = false,
            IsMetaQuiet = isMeta,
            IsCollapsible = isThought || isMeta,
            SuggestedAction = string.IsNullOrWhiteSpace(approvalReason)
                ? actionLabel
                : $"{actionLabel} · {approvalReason}",
            ApprovalText = string.IsNullOrWhiteSpace(approvalText) ? Loc.T("chat.no_title") : approvalText,
            CardTitle = string.IsNullOrWhiteSpace(cardTitle) ? null : cardTitle,
            Status = status,
            IsExpanded = isToolPending || failed || isChoice || isConfirm || approving,
            HeaderText = Truncate(
                interactive || failed
                    ? $"{approvalText} · {actionLabel}"
                    : body.Replace('\r', ' ').Replace('\n', ' '),
                72),
            CopyLabel = Loc.T("app.copy"),
            ChoiceOptions = choices
        };
    }

    /// <summary>Local optimistic lock so poll/UI cannot double-submit while Runtime flips to approving.</summary>
    public SessionEntryVm WithApprovingLock() =>
        new()
        {
            EntryId = EntryId,
            Type = Type,
            Timestamp = Timestamp,
            PayloadPreview = PayloadPreview,
            DisplayBody = DisplayBody,
            HeaderText = HeaderText,
            SummaryLabel = Loc.T("chat.approving"),
            IsPendingApproval = IsPendingApproval,
            IsChoiceCard = IsChoiceCard,
            IsConfirmCard = IsConfirmCard,
            IsFailedApproval = false,
            IsApproving = true,
            ShowDecisionButtons = false,
            ApprovalStatusLabel = Loc.T("chat.approving"),
            ApproveLabel = ApproveLabel,
            RejectLabel = RejectLabel,
            RetryLabel = RetryLabel,
            FailMessage = FailMessage,
            IsUser = IsUser,
            IsAssistant = IsAssistant,
            IsThought = IsThought,
            IsToolChip = IsToolChip,
            IsToolGroup = IsToolGroup,
            IsTodoCard = IsTodoCard,
            IsMetaQuiet = IsMetaQuiet,
            IsCollapsible = IsCollapsible,
            SuggestedAction = SuggestedAction,
            ApprovalText = ApprovalText,
            CardTitle = CardTitle,
            Status = "approving",
            IsExpanded = true,
            CopyLabel = CopyLabel,
            ChoiceOptions = ChoiceOptions,
            ToolRows = ToolRows,
            TodoItems = TodoItems
        };

    /// <summary>Merge consecutive thinking entries into one collapsible row (Cursor-style).</summary>
    public static SessionEntryVm MergeThoughts(
        IReadOnlyList<SessionEntryVm> thoughts,
        TimeSpan? duration = null)
    {
        if (thoughts.Count == 0)
        {
            throw new ArgumentException("thoughts required", nameof(thoughts));
        }

        var body = thoughts.Count == 1
            ? thoughts[0].DisplayBody
            : string.Join("\n\n——\n\n", thoughts.Select(t => t.DisplayBody).Where(s => !string.IsNullOrWhiteSpace(s)));
        var first = thoughts[0];
        return new SessionEntryVm
        {
            EntryId = first.EntryId,
            Type = first.Type,
            Timestamp = first.Timestamp,
            PayloadPreview = body,
            DisplayBody = body,
            HeaderText = Truncate(body.Replace('\r', ' ').Replace('\n', ' '), 72),
            SummaryLabel = FormatThoughtLabel(duration, thoughts.Count),
            IsThought = true,
            IsCollapsible = true,
            IsExpanded = false,
            CopyLabel = Loc.T("app.copy")
        };
    }

    private static string FormatThoughtLabel(TimeSpan? duration, int segments)
    {
        if (duration is { TotalSeconds: < 2 })
        {
            return Loc.T("chat.thought_brief");
        }

        if (duration is { } d && d.TotalSeconds >= 2)
        {
            var secs = (int)Math.Round(d.TotalSeconds);
            return Loc.T("chat.thought_secs", secs);
        }

        return segments > 1 ? Loc.T("chat.thought_n", segments) : Loc.T("chat.thought");
    }

    private static string FormatActionLabel(string? action, string? folder) => action switch
    {
        "archive_or_delete" => Loc.T("chat.suggest_archive"),
        "keep_important" => Loc.T("chat.suggest_keep"),
        "review" => Loc.T("chat.suggest_review"),
        "create_folder" => string.IsNullOrWhiteSpace(folder)
            ? Loc.T("chat.suggest_create_folder")
            : Loc.T("chat.suggest_create_folder_named", folder),
        "move_to_folder" => string.IsNullOrWhiteSpace(folder)
            ? Loc.T("chat.suggest_move")
            : Loc.T("chat.suggest_move_named", folder),
        null or "" => Loc.T("chat.suggest_confirm"),
        _ => action
    };

    private static string CompactJson(JsonElement element)
    {
        try
        {
            return JsonSerializer.Serialize(element);
        }
        catch
        {
            return element.GetRawText();
        }
    }

    private static string ExtractReadable(string preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(preview);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "text", "content", "message", "result" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        return v.GetString() ?? preview;
                    }
                }
            }
        }
        catch
        {
            // keep raw
        }

        return preview;
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Length <= max ? text : text[..max] + "…";
    }
}
