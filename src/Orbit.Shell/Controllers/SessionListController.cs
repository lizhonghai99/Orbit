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

internal sealed class SessionListController
{
    private readonly MainWindow _host;
    private int _pollInFlight;

    public SessionListController(MainWindow host) => _host = host;

    internal void WorkspaceChip_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = Loc.T("ws.open_folder") };
        openItem.Click += (_, _) =>
            UiAsync.Run(OpenWorkspaceFolderAsync,
                ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));
        menu.Items.Add(openItem);

        var state = _host._workspaceState;
        if (state?.Items is { Count: > 0 })
        {
            menu.Items.Add(new Separator());
            foreach (var item in state.Items
                         .OrderByDescending(i => i.LastOpenedAt)
                         .Take(8))
            {
                var isActive = string.Equals(item.Id, state.ActiveId, StringComparison.OrdinalIgnoreCase);
                var mi = new MenuItem
                {
                    Header = isActive ? $"✓ {item.Name}" : item.Name,
                    Tag = item.Id
                };
                ToolTip.SetTip(mi, item.Path);
                var id = item.Id;
                mi.Click += (_, _) =>
                    UiAsync.Run(() => ActivateWorkspaceAsync(id),
                        ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));
                menu.Items.Add(mi);
            }
        }

        if (sender is Control anchor)
        {
            menu.Open(anchor);
        }
    }

    internal async Task RefreshWorkspaceChipAsync()
    {
        try
        {
            _host._workspaceState = await _host._client.GetWorkspacesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host._workspaceState = null;
            _host.StatusText.Text = Loc.T("ws.load_failed", ConversationController.Shorten(ex.Message, 100));
        }

        ApplyWorkspaceChipText();
    }

    internal void ApplyWorkspaceChipText()
    {
        var active = _host._workspaceState?.Items.FirstOrDefault(i =>
            string.Equals(i.Id, _host._workspaceState?.ActiveId, StringComparison.OrdinalIgnoreCase));
        if (active is null)
        {
            _host.WorkspaceChipText.Text = Loc.T("ws.none");
            ToolTip.SetTip(_host.WorkspaceChipButton, Loc.T("ws.tip"));
            return;
        }

        _host.WorkspaceChipText.Text = active.Name;
        ToolTip.SetTip(_host.WorkspaceChipButton, active.Path);
    }

    private async Task OpenWorkspaceFolderAsync()
    {
        var folders = await _host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.T("ws.pick_folder"),
            AllowMultiple = false
        }).ConfigureAwait(true);

        var folder = folders.FirstOrDefault();
        var path = folder?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            _host._busy = true;
            var result = await MutateWorkspaceWithConfirmAsync(
                () => _host._client.OpenWorkspaceAsync(path),
                () => _host._client.OpenWorkspaceAsync(path, confirm: true)).ConfigureAwait(true);
            if (result is null)
            {
                return;
            }

            await RefreshWorkspaceChipAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            _host.StatusText.Text = FormatWorkspaceMutationStatus(result);
            _host.Conversation.UpdateConversationHeader();
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = LocalizeWorkspaceError(ex);
        }
        finally
        {
            _host._busy = false;
        }
    }

    private async Task ActivateWorkspaceAsync(string id)
    {
        try
        {
            _host._busy = true;
            var result = await MutateWorkspaceWithConfirmAsync(
                () => _host._client.ActivateWorkspaceAsync(id),
                () => _host._client.ActivateWorkspaceAsync(id, confirm: true)).ConfigureAwait(true);
            if (result is null)
            {
                return;
            }

            await RefreshWorkspaceChipAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            _host.StatusText.Text = FormatWorkspaceMutationStatus(result);
            _host.Conversation.UpdateConversationHeader();
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = LocalizeWorkspaceError(ex);
        }
        finally
        {
            _host._busy = false;
        }
    }

    private async Task<WorkspaceMutationResult?> MutateWorkspaceWithConfirmAsync(
        Func<Task<WorkspaceMutationResult>> attempt,
        Func<Task<WorkspaceMutationResult>> confirmed)
    {
        try
        {
            return await attempt().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsConfirmRequired(ex))
        {
            var ok = await ConfirmInterruptDialogAsync(ex).ConfigureAwait(true);
            if (!ok)
            {
                _host.StatusText.Text = Loc.T("ws.switch_cancelled");
                return null;
            }

            return await confirmed().ConfigureAwait(true);
        }
    }

    private static bool IsConfirmRequired(Exception ex) =>
        ex is OrbitApiException api && api.Is(OrbitErrorCodes.ConfirmRequired)
        || ex is InvalidOperationException ioe
           && ioe.Message.StartsWith(OrbitErrorCodes.ConfirmRequired, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> ConfirmInterruptDialogAsync(Exception serverError)
    {
        var tcs = new TaskCompletionSource<bool>();
        var confirm = new Window
        {
            Title = Loc.T("ws.confirm_title"),
            Width = 420,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = BuildConfirmPanel(ShortConfirmHint(serverError), out var ok, out var cancel)
        };
        ok.Click += (_, _) => { tcs.TrySetResult(true); confirm.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); confirm.Close(); };
        confirm.Closed += (_, _) => tcs.TrySetResult(false);
        await confirm.ShowDialog(_host).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }

    private static string ShortConfirmHint(Exception error)
    {
        if (error is OrbitApiException api && api.Details is not null)
        {
            var n = api.Details.TryGetValue("activeRuns", out var runs) ? runs : "?";
            var agents = api.Details.TryGetValue("agents", out var a) ? a : "?";
            return Loc.T("ws.confirm_interrupt_detail", n, agents);
        }

        // Structured Details missing (legacy body) — generic prompt, no regex scrape.
        return Loc.T("ws.confirm_interrupt_detail", "?", "?");
    }

    private static string LocalizeWorkspaceError(Exception error)
    {
        if (error is OrbitApiException api)
        {
            if (api.Is(OrbitErrorCodes.WorkspaceMissing))
            {
                return Loc.T("ws.missing");
            }

            if (api.Is(OrbitErrorCodes.WorkspaceMismatch))
            {
                return Loc.T("ws.mismatch");
            }

            if (api.Is(OrbitErrorCodes.WorkspaceActiveLocked))
            {
                return Loc.T("ws.cannot_remove_active");
            }
        }

        return LocalizeWorkspaceError(error.Message);
    }

    private static string LocalizeWorkspaceError(string message)
    {
        // Prefer stable codes (prefix); no English prose sniffing.
        if (message.StartsWith(OrbitErrorCodes.WorkspaceMissing, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("ws.missing");
        }

        if (message.StartsWith(OrbitErrorCodes.WorkspaceMismatch, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("ws.mismatch");
        }

        if (message.StartsWith(OrbitErrorCodes.WorkspaceActiveLocked, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("ws.cannot_remove_active");
        }

        return message;
    }

    private static string FormatWorkspaceMutationStatus(WorkspaceMutationResult result)
    {
        var name = result.Active?.Name ?? Loc.T("ws.none");
        if (result.RestartFailures is { Count: > 0 })
        {
            var first = result.RestartFailures.First();
            return Loc.T("ws.restart_failed", name, first.Key, ConversationController.Shorten(first.Value, 80));
        }

        if (result.RestartedAgents is { Count: > 0 })
        {
            return Loc.T("ws.restarted", name, string.Join(", ", result.RestartedAgents));
        }

        if (result.RequiresRestartAgents is { Count: > 0 })
        {
            return Loc.T("ws.switched_restart", name, string.Join(", ", result.RequiresRestartAgents));
        }

        return Loc.T("ws.switched", name);
    }

    internal static string LocalizeCreateSessionError(Exception error)
    {
        if (error is OrbitApiException api)
        {
            if (api.Is(OrbitErrorCodes.WorkspaceRequired))
            {
                return Loc.T("ws.required");
            }

            if (api.Is(OrbitErrorCodes.WorkspaceMissing))
            {
                return Loc.T("ws.missing");
            }

            if (api.Is(OrbitErrorCodes.WorkspaceMismatch))
            {
                return Loc.T("ws.mismatch");
            }
        }

        return LocalizeCreateSessionError(error.Message);
    }

    internal static string LocalizeCreateSessionError(string message)
    {
        if (message.StartsWith(OrbitErrorCodes.WorkspaceRequired, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("ws.required");
        }

        if (message.StartsWith(OrbitErrorCodes.WorkspaceMissing, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("ws.missing");
        }

        if (message.StartsWith(OrbitErrorCodes.WorkspaceMismatch, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("ws.mismatch");
        }

        return message;
    }

    internal void ShowArchivedCheck_OnIsCheckedChanged(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            _host._showArchived = _host.ShowArchivedCheck.IsChecked == true;
            await RefreshSessionsAsync().ConfigureAwait(true);
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void ArchiveSessionMenu_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(() => ArchiveSessionMenuCoreAsync(),
            ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    private async Task ArchiveSessionMenuCoreAsync()
    {
        if (_host.SelectedSessionSummary() is not { } summary)
        {
            return;
        }

        if (summary.IsArchived)
        {
            _host.StatusText.Text = Loc.T("status.session_archived");
            return;
        }

        try
        {
            _host._busy = true;
            _host.StatusText.Text = Loc.T("app.running");
            var detail = await _host._client.ArchiveSessionAsync(summary.SessionId).ConfigureAwait(true);
            await RefreshSessionsAsync().ConfigureAwait(true);
            if (_host._showArchived)
            {
                SelectSession(summary.SessionId);
                await _host.Conversation.LoadEntriesAsync(summary.SessionId, force: true, scrollToEnd: true).ConfigureAwait(true);
            }
            else
            {
                _host.Conversation.ClearConversationPane();
            }

            var summaryText = detail?.Header.Summary;
            _host.StatusText.Text = string.IsNullOrWhiteSpace(summaryText)
                ? Loc.T("status.session_archived")
                : Loc.T("status.archive_summary", ConversationController.TruncateStatus(summaryText, 80));
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = ex.Message;
        }
        finally
        {
            _host._busy = false;
        }
    }

    internal void DeleteSessionMenu_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(() => DeleteSessionMenuCoreAsync(sender, e),
            ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    private async Task DeleteSessionMenuCoreAsync(object? sender, RoutedEventArgs e)
    {
        if (_host.SelectedSessionSummary() is not { } summary)
        {
            return;
        }

        var confirm = new Window
        {
            Title = Loc.T("app.delete_session"),
            Width = 360,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = BuildConfirmPanel(Loc.T("status.session_delete_confirm"), out var ok, out var cancel)
        };
        var tcs = new TaskCompletionSource<bool>();
        ok.Click += (_, _) => { tcs.TrySetResult(true); confirm.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); confirm.Close(); };
        confirm.Closed += (_, _) => tcs.TrySetResult(false);
        await confirm.ShowDialog(_host).ConfigureAwait(true);
        if (!await tcs.Task.ConfigureAwait(true))
        {
            return;
        }

        try
        {
            if (string.Equals(_host._busySessionId, summary.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                await _host._client.CancelSessionAsync(summary.SessionId).ConfigureAwait(true);
            }

            await _host._client.DeleteSessionAsync(summary.SessionId).ConfigureAwait(true);
            if (string.Equals(_host._lastSessionId, summary.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                _host.Conversation.ClearConversationPane();
            }

            await RefreshSessionsAsync().ConfigureAwait(true);
            _host.StatusText.Text = Loc.T("status.session_deleted");
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = ex.Message;
        }
    }

    internal static StackPanel BuildConfirmPanel(string message, out Button ok, out Button cancel)
    {
        ok = new Button { Content = Loc.T("app.ok"), MinWidth = 72 };
        cancel = new Button { Content = Loc.T("app.cancel"), MinWidth = 72 };
        cancel.Classes.Add("ghost");
        return new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, ok }
                }
            }
        };
    }

    internal async Task RefreshSessionsAsync()
    {
        try
        {
            _host._allSessions = await _host._client.GetSessionsAsync(_host._showArchived).ConfigureAwait(true) ?? [];
            ApplySessionFilter(force: true);
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = ex.Message;
        }
    }

    internal void AgentsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host._suppressListEvents)
            {
                return;
            }

            ApplySessionFilter(force: true);
            // Running + no session → auto-create; otherwise open the newest (History can switch).
            await EnsureConversationForSelectedAgentAsync(createIfMissing: true).ConfigureAwait(true);
            _host.Conversation.UpdateConversationHeader();
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void AgentsList_OnDoubleTapped(object? sender, TappedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host._busy || _host.AgentsList.SelectedItem is not AgentInfo agent)
            {
                return;
            }

            try
            {
                _host._busy = true;
                if (agent.Lifecycle == AgentLifecycle.Running)
                {
                    _host.StatusText.Text = Loc.T("status.unloading", agent.DisplayName);
                    await _host._client.StopAgentAsync(agent.Id).ConfigureAwait(true);
                    await RefreshAsync().ConfigureAwait(true);
                    _host.StatusText.Text = Loc.T("status.unloaded", agent.DisplayName);
                }
                else
                {
                    _host.StatusText.Text = Loc.T("status.loading", agent.DisplayName);
                    await _host._client.StartAgentAsync(agent.Id).ConfigureAwait(true);
                    await RefreshAsync().ConfigureAwait(true);
                    await EnsureConversationForSelectedAgentAsync(createIfMissing: true).ConfigureAwait(true);
                    _host.StatusText.Text = Loc.T("status.loaded", agent.DisplayName);
                }

                _host.Conversation.UpdateConversationHeader();
            }
            catch (Exception ex)
            {
                _host.StatusText.Text = ex.Message;
            }
            finally
            {
                _host._busy = false;
            }
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void NewSessionButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host.AgentsList.SelectedItem is not AgentInfo agent)
            {
                _host.StatusText.Text = Loc.T("status.pick_agent");
                return;
            }

            if (agent.Lifecycle != AgentLifecycle.Running)
            {
                _host.StatusText.Text = Loc.T("status.load_agent_first");
                return;
            }

            try
            {
                _host._busy = true;
                var detail = await _host._client.CreateSessionAsync(
                    agent.Id,
                    title: null).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);
                if (detail is not null)
                {
                    SelectSession(detail.Header.SessionId);
                    await _host.Conversation.LoadEntriesAsync(detail.Header.SessionId, force: true, scrollToEnd: true)
                        .ConfigureAwait(true);
                }

                _host.StatusText.Text = Loc.T("status.new_chat_created");
                _host.Conversation.UpdateConversationHeader();
            }
            catch (Exception ex)
            {
                _host.StatusText.Text = LocalizeCreateSessionError(ex);
            }
            finally
            {
                _host._busy = false;
            }
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    /// <summary>
    /// Open the latest session for the selected agent, or create one when loading.
    /// Clicking an existing History item continues that chat (same session id).
    /// </summary>

    internal async Task EnsureConversationForSelectedAgentAsync(bool createIfMissing)
    {
        try
        {
            if (_host.AgentsList.SelectedItem is not AgentInfo agent
                || agent.Lifecycle != AgentLifecycle.Running)
            {
                return;
            }

            var latest = _host._allSessions
                .Where(s => string.Equals(s.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.UpdatedAt)
                .FirstOrDefault();

            if (latest is not null)
            {
                // Prefer keeping the user's current selection if it already belongs to this agent.
                if (_host.SelectedSessionSummary() is { } selected
                    && string.Equals(selected.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (_host._lastSessionId != selected.SessionId || _host.EntriesList.ItemsSource is null)
                    {
                        await _host.Conversation.LoadEntriesAsync(selected.SessionId, force: true, scrollToEnd: true)
                            .ConfigureAwait(true);
                    }

                    return;
                }

                SelectSession(latest.SessionId);
                await _host.Conversation.LoadEntriesAsync(latest.SessionId, force: true, scrollToEnd: true)
                    .ConfigureAwait(true);
                return;
            }

            if (!createIfMissing)
            {
                return;
            }

            var detail = await _host._client.CreateSessionAsync(agent.Id, title: null).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            if (detail is not null)
            {
                SelectSession(detail.Header.SessionId);
                await _host.Conversation.LoadEntriesAsync(detail.Header.SessionId, force: true, scrollToEnd: true)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = LocalizeCreateSessionError(ex);
        }
    }

    internal void SessionsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host._suppressListEvents)
            {
                return;
            }

            _host.Conversation.UpdateConversationHeader();
            if (_host.SelectedSessionSummary() is not { } summary)
            {
                _host.EntriesList.ItemsSource = null;
                _host._lastSessionId = null;
                _host._lastEntryCount = -1;
                _host._lastDetail = null;
                _host.Conversation.RefreshContextUsageUi();
                return;
            }

            // Same session still selected — do not rebuild the conversation (avoids flicker).
            if (summary.SessionId == _host._lastSessionId && _host.EntriesList.ItemsSource is not null)
            {
                return;
            }

            try
            {
                await _host.Conversation.LoadEntriesAsync(summary.SessionId, force: true, scrollToEnd: true)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _host.StatusText.Text = ex.Message;
            }
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal async Task PollAsync()
    {
        if (_host._suppressListEvents)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0)
        {
            return; // previous poll still running (1.2s busy interval can overlap)
        }

        try
        {
            // During an in-flight run, conversation updates arrive via SSE — do not full-pull here.
            if (_host._busy)
            {
                return;
            }

            var health = await _host._client.GetHealthAsync().ConfigureAwait(true);
            if (health is null)
            {
                var status = _host.RuntimeStatusText.Text ?? "";
                var offline = Loc.T("status.runtime_offline");
                if (!string.Equals(status, offline, StringComparison.Ordinal))
                {
                    _host.RuntimeStatusText.Text = offline;
                }

                return;
            }

            var desiredStatus = Loc.T("status.runtime_online", health.RepoRoot);
            if (!string.Equals(_host.RuntimeStatusText.Text, desiredStatus, StringComparison.Ordinal))
            {
                _host.RuntimeStatusText.Text = desiredStatus;
            }

            var selectedAgentId = (_host.AgentsList.SelectedItem as AgentInfo)?.Id;
            var selectedSessionId = _host.SelectedSessionSummary()?.SessionId
                                    ?? _host._lastSessionId;

            var agents = await _host._client.GetAgentsAsync().ConfigureAwait(true) ?? [];
            await ApplyAgentsAsync(agents, selectedAgentId, selectFirstIfMissing: false).ConfigureAwait(true);

            _host._allSessions = await _host._client.GetSessionsAsync(_host._showArchived).ConfigureAwait(true) ?? [];
            ApplySessionFilter(force: false);
            RestoreSessionSelection(selectedSessionId);

            // Only refetch conversation when entry count actually grows/changes.
            if (selectedSessionId is not null)
            {
                var summary = _host._allSessions.FirstOrDefault(s => s.SessionId == selectedSessionId);
                if (summary is null || summary.EntryCount != _host._lastEntryCount || selectedSessionId != _host._lastSessionId)
                {
                    await _host.Conversation.LoadEntriesAsync(selectedSessionId, force: false, scrollToEnd: false).ConfigureAwait(true);
                }
            }

            _host.Conversation.UpdateConversationHeader();
        }
        catch
        {
            // keep UI quiet during background poll
        }
        finally
        {
            Interlocked.Exchange(ref _pollInFlight, 0);
        }
    }

    internal async Task RefreshAsync()
    {
        var health = await _host._client.GetHealthAsync().ConfigureAwait(true);
        if (health is null)
        {
            _host.RuntimeStatusText.Text = Loc.T("status.runtime_offline");
            _host._suppressListEvents = true;
            try
            {
                _host.AgentsList.ItemsSource = null;
                _host.SessionsList.ItemsSource = null;
            }
            finally
            {
                _host._suppressListEvents = false;
            }

            _host.EntriesList.ItemsSource = null;
            _host.Trajectory.BindTrajectory(null, scrollToEnd: false);
            _host._allSessions = [];
            _host._agentsFingerprint = null;
            _host._sessionsFingerprint = null;
            _host._lastSessionId = null;
            _host._lastEntryCount = -1;
            _host.Conversation.UpdateConversationHeader();
            return;
        }

        _host.RuntimeStatusText.Text = Loc.T("status.runtime_online", health.RepoRoot);
        await RefreshWorkspaceChipAsync().ConfigureAwait(true);

        var selectedAgentId = (_host.AgentsList.SelectedItem as AgentInfo)?.Id;
        var selectedSessionId = _host.SelectedSessionSummary()?.SessionId;
        var agents = await _host._client.GetAgentsAsync().ConfigureAwait(true) ?? [];
        await ApplyAgentsAsync(agents, selectedAgentId, selectFirstIfMissing: true).ConfigureAwait(true);

        try
        {
            var manifestErrors = await _host._client.GetManifestErrorsAsync().ConfigureAwait(true);
            if (manifestErrors.Count > 0)
            {
                var first = manifestErrors[0];
                _host.StatusText.Text = Loc.T(
                    "agents.manifest_errors",
                    manifestErrors.Count,
                    first.File,
                    first.Error);
            }
        }
        catch
        {
            // diagnostics only
        }

        _host._allSessions = await _host._client.GetSessionsAsync(_host._showArchived).ConfigureAwait(true) ?? [];
        ApplySessionFilter(force: true);
        RestoreSessionSelection(selectedSessionId);
        _host.Conversation.UpdateConversationHeader();
    }

    internal Task ApplyAgentsAsync(List<AgentInfo> agents, string? selectedAgentId, bool selectFirstIfMissing)
    {
        var fingerprint = string.Join("|", agents.Select(a => $"{a.Id}:{a.Lifecycle}:{a.BaseUrl}"));
        if (fingerprint == _host._agentsFingerprint
            && _host.AgentsList.ItemsSource is not null
            && (selectedAgentId is null || (_host.AgentsList.SelectedItem as AgentInfo)?.Id == selectedAgentId))
        {
            return Task.CompletedTask;
        }

        _host._agentsFingerprint = fingerprint;
        _host._suppressListEvents = true;
        try
        {
            var localized = LocalizeAgents(agents);
            _host.AgentsList.ItemsSource = localized;
            if (selectedAgentId is not null)
            {
                _host.AgentsList.SelectedItem = localized.FirstOrDefault(a => a.Id == selectedAgentId);
            }
            else if (selectFirstIfMissing && localized.Count > 0)
            {
                _host.AgentsList.SelectedItem = localized[0];
            }
        }
        finally
        {
            _host._suppressListEvents = false;
        }

        return Task.CompletedTask;
    }

    internal void ApplySessionFilter(bool force)
    {
        var agentId = (_host.AgentsList.SelectedItem as AgentInfo)?.Id;
        var filtered = string.IsNullOrWhiteSpace(agentId)
            ? _host._allSessions
            : _host._allSessions.Where(s => string.Equals(s.AgentId, agentId, StringComparison.OrdinalIgnoreCase)).ToList();

        var fingerprint = string.Join("|", filtered.Select(s =>
            $"{s.SessionId}:{s.EntryCount}:{s.UpdatedAt:O}:{s.IsArchived}"));
        if (!force && fingerprint == _host._sessionsFingerprint && _host.SessionsList.ItemsSource is not null)
        {
            return;
        }

        var selectedSessionId = _host.SelectedSessionSummary()?.SessionId ?? _host._lastSessionId;
        _host._sessionsFingerprint = fingerprint;
        _host._suppressListEvents = true;
        try
        {
            _host.SessionsList.ItemsSource = filtered.Select(s => new SessionRowVm(s)).ToList();
            RestoreSessionSelection(selectedSessionId);
        }
        finally
        {
            _host._suppressListEvents = false;
        }
    }

    internal void RestoreSessionSelection(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || _host.SessionsList.ItemsSource is not IEnumerable<SessionRowVm> list)
        {
            return;
        }

        var match = list.FirstOrDefault(s => s.SessionId == sessionId);
        if (match is null)
        {
            return;
        }

        if (ReferenceEquals(_host.SessionsList.SelectedItem, match)
            || _host.SelectedSessionSummary()?.SessionId == sessionId)
        {
            return;
        }

        _host._suppressListEvents = true;
        try
        {
            _host.SessionsList.SelectedItem = match;
        }
        finally
        {
            _host._suppressListEvents = false;
        }
    }

    internal void SelectSession(string sessionId) => RestoreSessionSelection(sessionId);

    internal static List<AgentInfo> LocalizeAgents(IReadOnlyList<AgentInfo> agents) =>
        agents.Select(a => new AgentInfo
        {
            Id = a.Id,
            DisplayName = Loc.AgentName(a.Id, a.DisplayName),
            Description = Loc.AgentDescription(a.Id, a.Description),
            Capabilities = a.Capabilities,
            Protocol = a.Protocol,
            Lifecycle = a.Lifecycle,
            BaseUrl = a.BaseUrl,
            Port = a.Port,
            ProcessId = a.ProcessId,
            LastError = a.LastError
        }).ToList();

    internal async Task ReloadRunningAgentsAsync()
    {
        try
        {
            var result = await _host._client.RestartRunningAgentsAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            if (result.Failures.Count > 0)
            {
                var first = result.Failures.First();
                _host.StatusText.Text = Loc.T("status.llm_saved_reload_failed", $"{first.Key}: {first.Value}");
            }
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = Loc.T("status.llm_saved_reload_failed", ex.Message);
        }
    }
}
