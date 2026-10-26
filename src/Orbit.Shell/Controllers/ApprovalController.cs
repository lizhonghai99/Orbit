using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Orbit.Contracts;
using Orbit.Shell;
using Orbit.Shell.I18n;
using Orbit.Shell.Services;
using Orbit.Shell.ViewModels;

namespace Orbit.Shell.Controllers;

internal sealed class ApprovalController
{
    private readonly MainWindow _host;

    public ApprovalController(MainWindow host) => _host = host;

    internal void ApproveButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(() => DecideAsync(sender, "approve"),
            ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void ConfirmApproveButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(() => DecideAsync(sender, "approve", selected: "approved"),
            ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void ChoiceOptionButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (sender is not Button { Tag: ApprovalChoiceOptionVm opt })
            {
                return;
            }

            await DecideAsync(opt.EntryId, "approve", selected: opt.Label).ConfigureAwait(true);
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void RetryButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(() => DecideAsync(sender, "retry"),
            ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void RejectButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(() => DecideAsync(sender, "reject"),
            ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal async Task DecideAsync(object? sender, string decision, string? selected = null)
    {
        if (sender is not Button { Tag: string entryId })
        {
            return;
        }

        await DecideAsync(entryId, decision, selected).ConfigureAwait(true);
    }

    internal async Task DecideAsync(string entryId, string decision, string? selected = null)
    {
        if (_host.SelectedSessionSummary() is not { } summary)
        {
            return;
        }

        string? doneStatus = null;
        try
        {
            _host.Conversation.SetRunBusy(true, decision == "approve" || decision == "retry" ? Loc.T("status.approving") : Loc.T("status.rejecting"));
            _host.Conversation.LockApprovalCard(entryId);
            var detail = await _host._client.DecideApprovalAsync(
                    summary.SessionId,
                    entryId,
                    decision,
                    selected: selected)
                .ConfigureAwait(true);
            _host.Conversation.BindEntries(detail, scrollToEnd: true);
            var applied = detail?.Entries.LastOrDefault(e =>
                string.Equals(e.Type, "meta", StringComparison.OrdinalIgnoreCase)
                && e.Payload is System.Text.Json.JsonElement p
                && p.ValueKind == System.Text.Json.JsonValueKind.Object
                && p.TryGetProperty("kind", out var k)
                && k.GetString() == "approval_applied");
            if (decision == "reject")
            {
                doneStatus = Loc.T("status.rejected");
            }
            else if (applied?.Payload is System.Text.Json.JsonElement ap
                     && ap.TryGetProperty("message", out var msg))
            {
                doneStatus = msg.GetString() ?? Loc.T("status.approved_ran");
            }
            else
            {
                doneStatus = Loc.T("status.approved");
            }

            _host.Conversation.ScrollConversationToEnd();
        }
        catch (Exception ex)
        {
            doneStatus = ex.Message;
        }
        finally
        {
            _host.Conversation.SetRunBusy(false);
            if (!string.IsNullOrWhiteSpace(doneStatus))
            {
                _host.StatusText.Text = doneStatus;
            }
        }
    }

    internal static int CountPending(SessionDetail? detail) => CountApprovals(detail, "pending");

    internal static int CountQueued(SessionDetail? detail) => CountApprovals(detail, "queued");

    internal static int CountApprovals(SessionDetail? detail, string status) =>
        detail?.Entries.Count(e =>
            string.Equals(e.Type, "approval", StringComparison.OrdinalIgnoreCase)
            && e.Payload is System.Text.Json.JsonElement p
            && p.ValueKind == System.Text.Json.JsonValueKind.Object
            && p.TryGetProperty("status", out var s)
            && string.Equals(s.GetString(), status, StringComparison.OrdinalIgnoreCase)) ?? 0;
}
