using System.Collections.ObjectModel;
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

internal sealed class ConversationController
{
    private readonly MainWindow _host;

    public ConversationController(MainWindow host) => _host = host;

    internal void CopyMessageButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (sender is not Button { Tag: string text } || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var clipboard = TopLevel.GetTopLevel(_host)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            _host.StatusText.Text = Loc.T("status.copied");
        }, ex => _host.StatusText.Text = Shorten(ex.Message, 120));

    internal void ComposerPlusButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host.SelectedSessionSummary() is not { } summary)
            {
                _host.StatusText.Text = Loc.T("status.need_session_upload");
                await RefreshHarnessPanelAsync().ConfigureAwait(true);
                return;
            }

            var files = await _host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.T("status.upload_title"),
                AllowMultiple = false
            }).ConfigureAwait(true);

            var file = files.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            try
            {
                _host._busy = true;
                _host.StatusText.Text = Loc.T("status.uploading");
                var path = file.TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    _host.StatusText.Text = Loc.T("status.upload_no_path");
                    return;
                }

                var result = await _host._client.UploadAttachmentAsync(summary.SessionId, path).ConfigureAwait(true);
                await LoadEntriesAsync(summary.SessionId, force: true, scrollToEnd: true).ConfigureAwait(true);
                _host.StatusText.Text = result.ForwardedToAgent
                    ? Loc.T("status.uploaded_forwarded", result.Attachment.FileName)
                    : Loc.T("status.uploaded_msg", result.Attachment.FileName, result.AgentMessage ?? "");
            }
            catch (Exception ex)
            {
                _host.StatusText.Text = ex.Message;
            }
            finally
            {
                _host._busy = false;
            }
        }, ex => _host.StatusText.Text = Shorten(ex.Message, 120));

    internal void ClearConversationPane()
    {
        _host._lastSessionId = null;
        _host._lastDetail = null;
        _host._lastEntryCount = -1;
        _host.EntriesList.ItemsSource = null;
        _host.TrajectoryList.ItemsSource = null;
        _host.ConversationTitle.Text = Loc.T("app.pick_session");
        _host.ConversationSubtitle.Text = Loc.T("app.pick_session_sub");
    }

    internal static string TruncateStatus(string text, int max)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    internal void MessageBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        // Composer is plain chat text only (no slash-command palette).
    }

    internal void MessageBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter inserts a newline (default TextBox behavior when not handled).
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            UiAsync.Run(SendCurrentAsync, ex => _host.StatusText.Text = Shorten(ex.Message, 120));
        }
    }

    internal void SendButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host._busy)
            {
                await StopCurrentRunAsync().ConfigureAwait(true);
                return;
            }

            await SendCurrentAsync().ConfigureAwait(true);
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal async Task StopCurrentRunAsync()
    {
        if (_host._busySessionId is null)
        {
            return;
        }

        _host.StatusText.Text = Loc.T("status.stopping");
        try
        {
            await _host._client.CancelSessionAsync(_host._busySessionId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = ex.Message;
        }
    }

    internal async Task SendCurrentAsync()
    {
        // Cursor-style: while running, Enter queues a follow-up; Stop is the round button.
        if (_host._busy)
        {
            QueueFollowUpFromComposer();
            return;
        }

        var message = _host.MessageBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            _host.StatusText.Text = Loc.T("status.empty_message");
            return;
        }

        if (_host.SelectedSessionSummary() is not { } summary)
        {
            _host.StatusText.Text = Loc.T("status.load_before_send");
            return;
        }

        await RunTurnAsync(summary.SessionId, message).ConfigureAwait(true);
    }

    internal void QueueFollowUpFromComposer()
    {
        var text = _host.MessageBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            _host.StatusText.Text = Loc.T("status.still_busy");
            _host.StatusText.Foreground = new SolidColorBrush(Color.Parse("#1A2330"));
            return;
        }

        _host._pendingFollowUp = text;
        _host.MessageBox.Text = "";
        _host.StatusText.Text = Loc.T("status.followup_queued");
        _host.StatusText.Foreground = new SolidColorBrush(Color.Parse("#1A2330"));
        _host.StatusText.FontWeight = FontWeight.SemiBold;
    }

    internal async Task RunTurnAsync(string sessionId, string message)
    {
        _host.MessageBox.Text = "";
        AppendOptimisticUser(message);
        _host._busySessionId = sessionId;
        SetRunBusy(true, Loc.T("status.agent_busy"));
        // Idle poll stays at 3s for lists; conversation progress comes from SSE (not 1.2s full pulls).
        await StopSessionEventsWatchAsync().ConfigureAwait(true);
        _host._sessionEventsCts = new CancellationTokenSource();
        var watchCt = _host._sessionEventsCts.Token;
        var afterId = _host._lastDetail?.Entries is { Count: > 0 } e ? e[^1].Id : "";

        string? doneStatus = null;
        var interrupted = false;
        Task? watchTask = null;
        try
        {
            watchTask = Task.Run(async () =>
            {
                try
                {
                    await _host._client.WatchSessionEventsAsync(
                            sessionId,
                            afterId,
                            async (sync, ct) =>
                            {
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                                {
                                    if (!string.Equals(
                                            sessionId,
                                            _host._busySessionId,
                                            StringComparison.OrdinalIgnoreCase))
                                    {
                                        return;
                                    }

                                    if (sync.FullSnapshot)
                                    {
                                        BindEntries(
                                            new SessionDetail
                                            {
                                                Header = sync.Header,
                                                Entries = sync.Entries
                                            },
                                            scrollToEnd: true);
                                        return;
                                    }

                                    if (SessionEntrySync.RequiresFullFetch(sync))
                                    {
                                        await LoadEntriesAsync(sessionId, force: true, scrollToEnd: true)
                                            .ConfigureAwait(true);
                                        return;
                                    }

                                    BindEntries(
                                        SessionEntrySync.ApplySync(_host._lastDetail, sync),
                                        scrollToEnd: true,
                                        incremental: true);
                                });
                            },
                            watchCt)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (watchCt.IsCancellationRequested)
                {
                    // turn finished
                }
                catch
                {
                    // SSE failed — final BindEntries from run result still applies
                }
            }, watchCt);

            var detail = await _host._client.RunSessionAsync(sessionId, message).ConfigureAwait(true);
            try
            {
                _host._sessionEventsCts.Cancel();
                await watchTask.ConfigureAwait(true);
            }
            catch
            {
                // ignore watch shutdown races
            }

            BindEntries(detail, scrollToEnd: true);
            _host.Sessions.SelectSession(sessionId);
            interrupted = HasRunInterrupted(detail);
            var pending = ApprovalController.CountPending(detail);
            var queued = ApprovalController.CountQueued(detail);
            doneStatus = interrupted
                ? Loc.T("status.stopped")
                : pending > 0
                    ? queued > 0
                        ? Loc.T("status.turn_done_approval_queue", queued)
                        : Loc.T("status.turn_done_approval")
                    : Loc.T("status.turn_done");
            ScrollConversationToEnd();
        }
        catch (Exception ex)
        {
            doneStatus = ex.Message;
            try
            {
                await LoadEntriesAsync(sessionId, force: true, scrollToEnd: true).ConfigureAwait(true);
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            await StopSessionEventsWatchAsync(watchTask).ConfigureAwait(true);
            SetRunBusy(false);
            _host._busySessionId = null;
            if (!string.IsNullOrWhiteSpace(doneStatus))
            {
                _host.StatusText.Text = doneStatus;
            }

            _ = _host.Sessions.RefreshAsync();
        }

        var followUp = _host._pendingFollowUp;
        _host._pendingFollowUp = null;
        if (!string.IsNullOrWhiteSpace(followUp))
        {
            await RunTurnAsync(sessionId, followUp).ConfigureAwait(true);
        }
    }

    internal static bool HasRunInterrupted(SessionDetail? detail)
    {
        if (detail?.Entries is null || detail.Entries.Count == 0)
        {
            return false;
        }

        for (var i = detail.Entries.Count - 1; i >= 0; i--)
        {
            var entry = detail.Entries[i];
            if (!string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase)
                || entry.Payload is not System.Text.Json.JsonElement payload
                || payload.ValueKind != System.Text.Json.JsonValueKind.Object
                || !payload.TryGetProperty("kind", out var kind))
            {
                continue;
            }

            var k = kind.GetString();
            if (string.Equals(k, "run_interrupted", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(k, "agui_run_finished", StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, "agui_run_started", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return false;
    }

    internal void SetRunBusy(bool running, string? label = null)
    {
        _host._busy = running;
        _host.RunBusyBanner.IsVisible = running;
        if (running)
        {
            _host._runStartedAt = DateTimeOffset.UtcNow;
            _host._runDotPhase = 0;
            _host.RunBusyLabel.Text = string.IsNullOrWhiteSpace(label) ? Loc.T("status.working") : label.Trim();
            _host.RunBusyDots.Text = "";
            _host.RunBusyElapsed.Text = Loc.T("status.elapsed_zero");
            _host.StatusText.Foreground = new SolidColorBrush(Color.Parse("#1A2330"));
            _host.StatusText.FontWeight = FontWeight.SemiBold;
            _host.StatusText.Text = $"{_host.RunBusyLabel.Text}…";
            _host.MessageBox.PlaceholderText = Loc.T("app.composer_followup");
            _host.SendButton.Classes.Add("run-busy");
            _host.SendButton.Content = Loc.T("app.send_stop");
            ToolTip.SetTip(_host.SendButton, Loc.T("status.stop_tip"));
            _host.SendButton.IsEnabled = true;
            if (!_host.RunBusySpinner.Classes.Contains("run-spin"))
            {
                _host.RunBusySpinner.Classes.Add("run-spin");
            }

            _host._runBusyTimer.Start();
            TickRunBusyUi();
        }
        else
        {
            _host._runBusyTimer.Stop();
            _host._runStartedAt = null;
            _host.RunBusyDots.Text = "";
            _host.RunBusyElapsed.Text = "";
            _host.StatusText.Foreground = new SolidColorBrush(Color.Parse("#7A8494"));
            _host.StatusText.FontWeight = FontWeight.Normal;
            _host.MessageBox.PlaceholderText = Loc.T("app.composer_placeholder");
            _host.SendButton.Classes.Remove("run-busy");
            _host.SendButton.Content = Loc.T("app.send_arrow");
            ToolTip.SetTip(_host.SendButton, Loc.T("status.send_tip"));
            _host.SendButton.IsEnabled = true;
        }
    }

    internal void TickRunBusyUi()
    {
        if (!_host._busy || _host._runStartedAt is null)
        {
            return;
        }

        _host._runDotPhase = (_host._runDotPhase + 1) % 4;
        _host.RunBusyDots.Text = _host._runDotPhase switch
        {
            1 => ".",
            2 => "..",
            3 => "...",
            _ => ""
        };

        _host.RunBusySpinner.Opacity = _host._runDotPhase % 2 == 0 ? 1.0 : 0.35;

        var elapsed = DateTimeOffset.UtcNow - _host._runStartedAt.Value;
        _host.RunBusyElapsed.Text = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {(int)elapsed.Seconds:D2}s"
            : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";
        _host.StatusText.Text = $"{_host.RunBusyLabel.Text}{_host.RunBusyDots.Text} · {_host.RunBusyElapsed.Text}";
    }

    internal void AppendOptimisticUser(string message)
    {
        var row = SessionEntryVm.CreateOptimisticUser(message);
        if (_host.EntriesList.ItemsSource is ObservableCollection<SessionEntryVm> obs)
        {
            obs.Add(row);
        }
        else
        {
            var existing = _host.EntriesList.ItemsSource as IEnumerable<SessionEntryVm>;
            var list = existing is null
                ? new ObservableCollection<SessionEntryVm>()
                : new ObservableCollection<SessionEntryVm>(existing);
            list.Add(row);
            _host.EntriesList.ItemsSource = list;
        }

        _host._lastEntryCount = -1; // force subsequent poll/load to replace with server entries
        ScrollConversationToEnd();
    }

    internal async Task LoadEntriesAsync(string sessionId, bool force, bool scrollToEnd)
    {
        // Drop stale responses after the user switched sessions mid-flight.
        bool StillCurrent()
        {
            var selected = _host.SelectedSessionSummary()?.SessionId ?? _host._lastSessionId;
            return string.Equals(sessionId, selected, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(sessionId, _host._busySessionId, StringComparison.OrdinalIgnoreCase);
        }

        // Incremental when we already hold this session and are not forcing a rebuild.
        string? afterId = null;
        if (!force
            && string.Equals(sessionId, _host._lastSessionId, StringComparison.OrdinalIgnoreCase)
            && _host._lastDetail?.Entries is { Count: > 0 } prior)
        {
            afterId = prior[^1].Id;
        }

        if (!string.IsNullOrWhiteSpace(afterId))
        {
            var sync = await _host._client.GetSessionSyncAsync(sessionId, afterId).ConfigureAwait(true);
            if (!StillCurrent())
            {
                return;
            }

            if (sync is null)
            {
                return;
            }

            if (!sync.FullSnapshot && sync.Entries.Count == 0)
            {
                // Header-only bump possible; keep count gate via summary poll.
                if (sync.Header.EntryCount == _host._lastEntryCount)
                {
                    return;
                }
            }

            if (sync.FullSnapshot)
            {
                BindEntries(
                    new SessionDetail { Header = sync.Header, Entries = sync.Entries },
                    scrollToEnd);
                return;
            }

            if (SessionEntrySync.RequiresFullFetch(sync))
            {
                var full = await _host._client.GetSessionAsync(sessionId).ConfigureAwait(true);
                if (!StillCurrent() || full is null)
                {
                    return;
                }

                BindEntries(full, scrollToEnd);
                return;
            }

            BindEntries(SessionEntrySync.ApplySync(_host._lastDetail, sync), scrollToEnd);
            return;
        }

        var detail = await _host._client.GetSessionAsync(sessionId).ConfigureAwait(true);
        if (!StillCurrent())
        {
            return;
        }

        var count = detail?.Entries.Count ?? 0;
        if (!force && sessionId == _host._lastSessionId && count == _host._lastEntryCount)
        {
            return;
        }

        BindEntries(detail, scrollToEnd);
    }

    /// <summary>Hide decision buttons immediately so poll / double-click cannot re-fire.</summary>
    internal void LockApprovalCard(string entryId)
    {
        if (_host.EntriesList.ItemsSource is ObservableCollection<SessionEntryVm> obs)
        {
            for (var i = 0; i < obs.Count; i++)
            {
                if (string.Equals(obs[i].EntryId, entryId, StringComparison.OrdinalIgnoreCase))
                {
                    obs[i] = obs[i].WithApprovingLock();
                    return;
                }
            }

            return;
        }

        if (_host.EntriesList.ItemsSource is not IEnumerable<SessionEntryVm> existing)
        {
            return;
        }

        var list = existing.Select(vm =>
                string.Equals(vm.EntryId, entryId, StringComparison.OrdinalIgnoreCase)
                    ? vm.WithApprovingLock()
                    : vm)
            .ToList();
        _host.EntriesList.ItemsSource = new ObservableCollection<SessionEntryVm>(list);
    }

    internal void BindEntries(SessionDetail? detail, bool scrollToEnd, bool incremental = false)
    {
        IReadOnlyList<SessionEntryVm>? vms = detail?.Entries is { Count: > 0 } entries
            ? ConversationProjector.Project(entries)
            : null;

        if (incremental
            && vms is not null
            && _host.EntriesList.ItemsSource is ObservableCollection<SessionEntryVm> existing
            && TryApplyProjectedIncrement(existing, vms))
        {
            // Reused ObservableCollection — Avalonia keeps row visuals for unchanged prefix.
        }
        else if (vms is null)
        {
            _host.EntriesList.ItemsSource = null;
        }
        else
        {
            _host.EntriesList.ItemsSource = new ObservableCollection<SessionEntryVm>(vms);
        }

        _host._lastDetail = detail;
        _host._lastSessionId = detail?.Header.SessionId;
        _host._lastEntryCount = detail?.Entries.Count ?? -1;
        var pending = ApprovalController.CountPending(detail);
        var queued = ApprovalController.CountQueued(detail);
        _host.ContextPendingText.Text = queued > 0
            ? Loc.T("status.pending_queued", pending, queued)
            : pending == 0 ? Loc.T("status.none") : Loc.T("status.pending_n", pending);
        RefreshContextUsageUi();
        // Trajectory is heavy; only rebuild when the trace pane is visible (or on full bind).
        if (!incremental || _host._traceView)
        {
            _host.Trajectory.BindTrajectory(detail, scrollToEnd);
        }

        if (scrollToEnd && detail is not null && !_host._traceView)
        {
            ScrollConversationToEnd();
        }
    }

    /// <summary>
    /// Cancel the SSE watch, await the runner (if known), then dispose the CTS.
    /// Disposing before the watch leaves ReadLineAsync avoids ObjectDisposedException races.
    /// </summary>
    private async Task StopSessionEventsWatchAsync(Task? watchTask = null)
    {
        var cts = _host._sessionEventsCts;
        if (cts is null)
        {
            if (watchTask is not null)
            {
                try
                {
                    await watchTask.ConfigureAwait(true);
                }
                catch
                {
                    // ignore
                }
            }

            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        if (watchTask is not null)
        {
            try
            {
                await watchTask.ConfigureAwait(true);
            }
            catch
            {
                // ignore watch shutdown races
            }
        }

        try
        {
            cts.Dispose();
        }
        catch
        {
            // ignore
        }

        if (ReferenceEquals(_host._sessionEventsCts, cts))
        {
            _host._sessionEventsCts = null;
        }
    }

    /// <summary>
    /// Mutate an existing collection in place when the projected id sequence is a compatible update.
    /// Returns false when a full ItemsSource replace is safer.
    /// </summary>
    private static bool TryApplyProjectedIncrement(
        ObservableCollection<SessionEntryVm> existing,
        IReadOnlyList<SessionEntryVm> projected)
    {
        var shared = 0;
        var limit = Math.Min(existing.Count, projected.Count);
        while (shared < limit
               && string.Equals(
                   existing[shared].EntryId,
                   projected[shared].EntryId,
                   StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        // Diverged in the middle — full replace.
        if (shared < existing.Count && shared < projected.Count)
        {
            return false;
        }

        for (var i = 0; i < shared; i++)
        {
            if (ProjectionLooksSame(existing[i], projected[i]))
            {
                continue;
            }

            var next = projected[i];
            next.IsExpanded = existing[i].IsExpanded;
            existing[i] = next;
        }

        while (existing.Count > shared)
        {
            existing.RemoveAt(existing.Count - 1);
        }

        for (var i = shared; i < projected.Count; i++)
        {
            existing.Add(projected[i]);
        }

        return true;
    }

    // Intentionally narrow: timestamp / expand state are ignored. Assigning existing[i]=next
    // is ObservableCollection remove+insert (row visual rebuild) — acceptable for rare in-place updates.
    private static bool ProjectionLooksSame(SessionEntryVm a, SessionEntryVm b) =>
        string.Equals(a.EntryId, b.EntryId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.DisplayBody, b.DisplayBody, StringComparison.Ordinal)
        && string.Equals(a.HeaderText, b.HeaderText, StringComparison.Ordinal)
        && string.Equals(a.SummaryLabel, b.SummaryLabel, StringComparison.Ordinal)
        && string.Equals(a.Status, b.Status, StringComparison.Ordinal)
        && a.IsPendingApproval == b.IsPendingApproval
        && a.ShowDecisionButtons == b.ShowDecisionButtons
        && a.IsApproving == b.IsApproving
        && a.TodoDoneCount == b.TodoDoneCount
        && a.TodoTotalCount == b.TodoTotalCount
        && a.ToolRows.Count == b.ToolRows.Count
        && (a.ToolRows.Count == 0
            || a.ToolRows.Zip(b.ToolRows).All(p =>
                string.Equals(p.First.Line, p.Second.Line, StringComparison.Ordinal)));

    internal void UsageStrip_OnClick(object? sender, RoutedEventArgs e)
    {
        _host.UsagePanel.IsVisible = !_host.UsagePanel.IsVisible;
        if (_host.UsagePanel.IsVisible)
        {
            RefreshContextUsageUi();
        }
    }

    internal void UsagePanelClose_OnClick(object? sender, RoutedEventArgs e) =>
        _host.UsagePanel.IsVisible = false;

    internal void RefreshContextUsageUi()
    {
        var model = _host.ModelBox.SelectedItem as string ?? _host.ModelBox.Text ?? LlmEnvStore.GetStatus().Model;
        var toolHint = _host.AgentsList.SelectedItem is AgentInfo a
            ? a.Id.Contains("mail", StringComparison.OrdinalIgnoreCase) ? 10 : 6
            : 6;

        var snap = ContextUsageEstimator.Estimate(_host._lastDetail, model, _host._lastSkillsChars, toolHint);
        _host._lastUsage = snap;

        var occ = ContextUsageEstimator.FormatTokens(snap.OccupancyTokens);
        var windowText = snap.WindowKnown
            ? ContextUsageEstimator.FormatTokens(snap.WindowTokens)
            : "?";
        var pct = !snap.WindowKnown
            ? "—"
            : snap.PercentFull >= 10
                ? $"{snap.PercentFull:0}%"
                : $"{snap.PercentFull:0.#}%";
        var approx = snap.IsEstimate ? "~" : "";
        var life = ContextUsageEstimator.FormatTokens(snap.LifetimeTokens);

        _host.UsageStripText.Text = snap.WindowKnown
            ? $"{pct} · {approx}{occ} / {windowText}"
            : Loc.T("usage.strip_unknown", approx, occ);
        _host.UsagePanelPercent.Text = snap.WindowKnown ? Loc.T("usage.pct_full", pct) : Loc.T("usage.window_unknown");
        _host.UsagePanelSource.Text = snap.IsEstimate ? Loc.T("app.estimate") : Loc.T("app.model_usage");
        _host.UsagePanelTokens.Text = snap.IsEstimate
            ? Loc.T("usage.occ_est", occ, windowText, life)
            : Loc.T("usage.occ", occ, windowText, life);

        FillUsageBar(_host.UsageStripBar, snap);
        FillUsageBar(_host.UsagePanelBar, snap);

        var rows = snap.Segments.Select(s => new ContextUsageRowVm
        {
            Label = s.Label,
            TokensText = ContextUsageEstimator.FormatTokens(s.Tokens),
            ColorBrush = new SolidColorBrush(Color.Parse(s.ColorHex))
        }).ToList();

        if (snap.LifetimeTokens > 0)
        {
            rows.Add(new ContextUsageRowVm
            {
                Label = Loc.T("usage.lifetime"),
                TokensText = life,
                ColorBrush = new SolidColorBrush(Color.Parse("#8E8E93"))
            });
        }

        _host.UsageBreakdownList.ItemsSource = rows;
    }

    internal static void FillUsageBar(Grid bar, ContextUsageSnapshot snap)
    {
        bar.Children.Clear();
        bar.ColumnDefinitions.Clear();
        if (snap.OccupancyTokens <= 0)
        {
            return;
        }

        // Bar reflects occupancy vs window (not lifetime spend / output).
        var fill = snap.Segments
            .Where(s => !s.Label.Contains("output", StringComparison.OrdinalIgnoreCase)
                        && !s.Label.Contains("Lifetime", StringComparison.OrdinalIgnoreCase)
                        && !s.Label.Contains(Loc.T("usage.seg.output"), StringComparison.Ordinal)
                        && !s.Label.Contains(Loc.T("usage.lifetime"), StringComparison.Ordinal))
            .ToList();
        if (fill.Count == 0)
        {
            fill = [new ContextUsageSegment(Loc.T("usage.seg.occupancy"), snap.OccupancyTokens, "#5AC8FA")];
        }

        var col = 0;
        foreach (var seg in fill)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(1, seg.Tokens), GridUnitType.Star)));
            var cell = new Border
            {
                Background = new SolidColorBrush(Color.Parse(seg.ColorHex)),
                Height = bar.Height > 0 ? bar.Height : 6
            };
            Grid.SetColumn(cell, col++);
            bar.Children.Add(cell);
        }

        if (snap.WindowKnown && snap.OccupancyTokens < snap.WindowTokens)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(
                Math.Max(1, snap.WindowTokens - snap.OccupancyTokens), GridUnitType.Star)));
            var empty = new Border { Background = Brushes.Transparent };
            Grid.SetColumn(empty, col);
            bar.Children.Add(empty);
        }
    }

    internal void UpdateConversationHeader()
    {
        if (_host.SelectedSessionSummary() is { } session)
        {
            _host.ConversationTitle.Text = SessionTitleDisplay.Display(session.Title);
            if (session.IsArchived && !string.IsNullOrWhiteSpace(session.Summary))
            {
                _host.ConversationSubtitle.Text = Loc.T("status.archive_summary", TruncateStatus(session.Summary!, 120));
            }
            else
            {
                var baseSub = Loc.T("chat.resumable", session.AgentId, session.UpdatedAt.ToLocalTime());
                _host.ConversationSubtitle.Text = string.IsNullOrWhiteSpace(session.WorkspacePath)
                    ? baseSub
                    : $"{baseSub} · {session.WorkspacePath}";
            }
        }
        else if (_host.AgentsList.SelectedItem is AgentInfo agentOnly)
        {
            _host.ConversationTitle.Text = agentOnly.DisplayName;
            _host.ConversationSubtitle.Text = agentOnly.Lifecycle == AgentLifecycle.Running
                ? Loc.T("chat.running_at", agentOnly.BaseUrl ?? "")
                : Loc.T("chat.dblclick_to_chat");
        }
        else
        {
            _host.ConversationTitle.Text = Loc.T("chat.select_agent");
            _host.ConversationSubtitle.Text = Loc.T("chat.select_agent_sub");
        }

        if (_host.AgentsList.SelectedItem is AgentInfo agent)
        {
            _host.ContextAgentText.Text = agent.Lifecycle == AgentLifecycle.Running
                ? $"{agent.DisplayName}\n{agent.Lifecycle} · {agent.BaseUrl}"
                : $"{agent.DisplayName}\n{agent.Lifecycle}";
            _ = RefreshHarnessPanelAsync();
        }
        else
        {
            _host.ContextAgentText.Text = "—";
            ResetHarnessPanel(Loc.T("harness.pick_agent"));
        }

        _host.ContextRuntimeText.Text = _host.RuntimeStatusText.Text ?? "—";
    }

    internal async Task RefreshHarnessPanelAsync()
    {
        await _host.Capabilities.RefreshAsync().ConfigureAwait(true);
        if (_host._traceView && _host._lastDetail is not null)
        {
            _host.Trajectory.BindTrajectory(_host._lastDetail, scrollToEnd: false);
        }

        RefreshContextUsageUi();
    }

    internal void ResetHarnessPanel(string styleHint)
    {
        _host.HarnessStyleText.Text = styleHint;
        _host.Capabilities.ShowNeedAgent();
    }

    /// <summary>Legacy entry point; capabilities panel now owns harness + tools + skills binding.</summary>
    internal void BindHarnessHealth(AgentProcessHealthDto? health) =>
        _ = _host.Capabilities.RefreshAsync();

    internal Brush ResolveBrush(string resourceKey, string fallbackHex)
    {
        if (_host.TryGetResource(resourceKey, _host.ActualThemeVariant, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    internal static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    internal static string FriendHarnessStyle(string style) =>
        style.Contains("hand-assembled", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("harness.style_hand", style.Replace("hand-assembled ", "", StringComparison.OrdinalIgnoreCase))
            : style;

    internal static (string Title, string Detail) FriendHarnessModule(HarnessModuleDto module)
    {
        var title = module.Id switch
        {
            "mode" => Loc.T("harness.mode"),
            "todo" => Loc.T("harness.todo"),
            "file_memory" => Loc.T("harness.file_memory"),
            "file_access" => Loc.T("harness.file_access"),
            "skills" => Loc.T("harness.skills"),
            "background_agents" => Loc.T("harness.background_agents"),
            "compaction" => Loc.T("harness.compaction"),
            "tool_approval" => Loc.T("harness.tool_approval"),
            _ => module.Title
        };

        var detail = module.Id switch
        {
            "mode" => $"{module.Title} · plan → execute",
            "todo" => $"{module.Title} · todos_*",
            "file_memory" => module.Detail ?? module.Title,
            "file_access" => module.Detail ?? Loc.T("harness.file_access_fallback"),
            "skills" => module.Enabled ? (module.Detail ?? "load_skill") : Loc.T("harness.skills_missing"),
            "background_agents" => module.Detail ?? "background_agents_*",
            "compaction" => module.Detail ?? "ContextWindow",
            "tool_approval" => Loc.T("harness.tool_approval_detail"),
            _ => module.Detail ?? module.Kind
        };

        return (title, detail);
    }

    internal static string FriendToolLine(KeyValuePair<string, string> kv) =>
        kv.Key switch
        {
            "capability" => Loc.T("harness.tool.capability", kv.Value),
            "loginUrl" => Loc.T("harness.tool.login", Shorten(kv.Value, 48)),
            "profileDir" => Loc.T("harness.tool.profile", Shorten(kv.Value, 40)),
            "headless" => string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase)
                ? Loc.T("harness.tool.headless")
                : Loc.T("harness.tool.headed"),
            _ => $"{kv.Key}：{Shorten(kv.Value, 48)}"
        };

    internal void ScrollConversationToEnd()
    {
        Dispatcher.UIThread.Post(() => _host.ConversationScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
