using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Orbit.Contracts;
using Orbit.Shell.Controllers;
using Orbit.Shell.I18n;
using Orbit.Shell.Services;
using Orbit.Shell.ViewModels;

namespace Orbit.Shell;

public partial class MainWindow : Window
{
    internal readonly RuntimeClient _client = new();
    internal readonly DispatcherTimer _pollTimer;
    internal readonly DispatcherTimer _runBusyTimer;
    internal List<SessionSummary> _allSessions = [];
    internal int _lastEntryCount = -1;
    internal string? _lastSessionId;
    internal string? _agentsFingerprint;
    internal string? _sessionsFingerprint;
    internal bool _busy;
    internal string? _busySessionId;
    /// <summary>Cancels <c>/sessions/{id}/events</c> watch when a turn ends.</summary>
    internal CancellationTokenSource? _sessionEventsCts;
    internal string? _pendingFollowUp;
    internal bool _modelBoxUpdating;
    internal bool _reasoningUiUpdating;
    internal bool _settingsLoading;
    internal bool _suppressListEvents;
    internal bool _showArchived;
    internal SessionDetail? _lastDetail;
    internal int _lastSkillsChars;
    internal ContextUsageSnapshot? _lastUsage;
    internal DateTimeOffset? _runStartedAt;
    internal int _runDotPhase;
    internal bool _traceView;
    internal string? _selectedTrajectoryRecordId;
    internal IReadOnlyList<TrajectoryRecordVm> _trajectoryRecords = [];
    internal Dictionary<string, string> _toolSchemaHints = new(StringComparer.OrdinalIgnoreCase);

    internal SessionListController Sessions { get; }
    internal ConversationController Conversation { get; }
    internal ApprovalController Approval { get; }
    internal SettingsController Settings { get; }
    internal TrajectoryController Trajectory { get; }
    internal CapabilitiesController Capabilities { get; }
    internal WorkspaceState? _workspaceState;

    internal SessionSummary? SelectedSessionSummary() =>
        SessionsList.SelectedItem switch
        {
            SessionRowVm row => row.Summary,
            SessionSummary s => s,
            _ => null
        };

    public MainWindow()
    {
        InitializeComponent();
        Sessions = new SessionListController(this);
        Conversation = new ConversationController(this);
        Approval = new ApprovalController(this);
        Settings = new SettingsController(this);
        Trajectory = new TrajectoryController(this);
        Capabilities = new CapabilitiesController(this);
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += (_, _) =>
            UiAsync.Run(() => Sessions.PollAsync(),
                ex => StatusText.Text = ConversationController.Shorten(ex.Message, 120));
        _runBusyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _runBusyTimer.Tick += (_, _) => Conversation.TickRunBusyUi();
        Opened += (_, _) => UiAsync.Run(async () =>
        {
            Loc.Initialize();
            Loc.Changed += OnLocaleChanged;
            UiText.Apply(this);
            if (await RuntimeLauncher.TryStartAsync().ConfigureAwait(true))
            {
                for (var i = 0; i < 25; i++)
                {
                    await Task.Delay(200).ConfigureAwait(true);
                    if (await _client.GetHealthAsync().ConfigureAwait(true) is not null)
                    {
                        break;
                    }
                }
            }

            // Runtime issues a fresh token on each start; reload after health is up.
            _client.AttachLocalToken();
            await Settings.SyncLlmStatusAsync().ConfigureAwait(true);
            Settings.RefreshLlmUi();
            Settings.RefreshReasoningUi();

            await Sessions.RefreshAsync().ConfigureAwait(true);
            _ = Settings.RefreshModelListQuietAsync();
            _pollTimer.Start();

            var status = LlmEnvStore.GetStatus();
            if (!status.HasApiKey)
            {
                Settings.OpenSettingsPanel();
                StatusText.Text = Loc.T("status.need_settings_key");
            }
        }, ex =>
        {
            try
            {
                StatusText.Text = Loc.T("status.runtime_offline") + " · " + ex.Message;
            }
            catch
            {
                // UI may not be ready
            }
        });
    }

    private void OnLocaleChanged()
    {
        UiText.Apply(this);
        Settings.RefreshReasoningUi();
        if (AgentsList.ItemsSource is IEnumerable<AgentInfo> agents)
        {
            var selectedId = (AgentsList.SelectedItem as AgentInfo)?.Id;
            var localized = SessionListController.LocalizeAgents(agents.ToList());
            _suppressListEvents = true;
            try
            {
                AgentsList.ItemsSource = localized;
                if (selectedId is not null)
                {
                    AgentsList.SelectedItem = localized.FirstOrDefault(a => a.Id == selectedId);
                }
            }
            finally
            {
                _suppressListEvents = false;
            }
        }

        Sessions.ApplySessionFilter(force: true);
        Conversation.UpdateConversationHeader();
        Conversation.RefreshContextUsageUi();
        _ = Capabilities.RefreshAsync();
        if (_lastDetail is not null)
        {
            Conversation.BindEntries(_lastDetail, scrollToEnd: false);
        }
        else
        {
            Trajectory.ClearTraceDetail();
        }
    }

    private void CapabilitiesRefresh_OnClick(object? sender, RoutedEventArgs e) =>
        _ = Capabilities.RefreshAsync();

    private void McpAddButton_OnClick(object? sender, RoutedEventArgs e) =>
        Capabilities.OpenAddMcpOverlay();

    private void CloseMcpOverlayButton_OnClick(object? sender, RoutedEventArgs e) =>
        Capabilities.CloseAddMcpOverlay();

    private void McpDim_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        Capabilities.CloseAddMcpOverlay();

    private void McpTransportBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Capabilities?.SyncMcpTransportFields(); // SelectionChanged can fire during InitializeComponent

    private void SaveMcpOverlayButton_OnClick(object? sender, RoutedEventArgs e) =>
        _ = Capabilities.SaveMcpOverlayAsync();

    private void McpRestartNowButton_OnClick(object? sender, RoutedEventArgs e) =>
        _ = Capabilities.RestartAgentNowAsync();

    // XAML event names stay here and forward into feature controllers.
    private void OpenSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        Settings.OpenSettingsButton_OnClick(sender, e);

    private void CopyMessageButton_OnClick(object? sender, RoutedEventArgs e) =>
        Conversation.CopyMessageButton_OnClick(sender, e);

    private void ComposerPlusButton_OnClick(object? sender, RoutedEventArgs e) =>
        Conversation.ComposerPlusButton_OnClick(sender, e);

    private void CloseSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        Settings.CloseSettingsButton_OnClick(sender, e);

    private void SettingsDim_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        Settings.SettingsDim_OnPointerPressed(sender, e);

    private void SettingsProfileBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Settings.SettingsProfileBox_OnSelectionChanged(sender, e);

    private void SettingsRefreshModelsButton_OnClick(object? sender, RoutedEventArgs e) =>
        Settings.SettingsRefreshModelsButton_OnClick(sender, e);

    private void SaveSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        Settings.SaveSettingsButton_OnClick(sender, e);

    private void OpenLogsButton_OnClick(object? sender, RoutedEventArgs e) =>
        Settings.OpenLogsButton_OnClick(sender, e);

    private void ReasoningToggle_OnIsCheckedChanged(object? sender, RoutedEventArgs e) =>
        Settings.ReasoningToggle_OnIsCheckedChanged(sender, e);

    private void ReasoningEffortBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Settings.ReasoningEffortBox_OnSelectionChanged(sender, e);

    private void ShowArchivedCheck_OnIsCheckedChanged(object? sender, RoutedEventArgs e) =>
        Sessions.ShowArchivedCheck_OnIsCheckedChanged(sender, e);

    private void ArchiveSessionMenu_OnClick(object? sender, RoutedEventArgs e) =>
        Sessions.ArchiveSessionMenu_OnClick(sender, e);

    private void DeleteSessionMenu_OnClick(object? sender, RoutedEventArgs e) =>
        Sessions.DeleteSessionMenu_OnClick(sender, e);

    private void ModelBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Settings.ModelBox_OnSelectionChanged(sender, e);

    private void AgentsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Sessions.AgentsList_OnSelectionChanged(sender, e);

    private void AgentsList_OnDoubleTapped(object? sender, TappedEventArgs e) =>
        Sessions.AgentsList_OnDoubleTapped(sender, e);

    private void NewSessionButton_OnClick(object? sender, RoutedEventArgs e) =>
        Sessions.NewSessionButton_OnClick(sender, e);

    private void WorkspaceChip_OnClick(object? sender, RoutedEventArgs e) =>
        Sessions.WorkspaceChip_OnClick(sender, e);

    private void SessionsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Sessions.SessionsList_OnSelectionChanged(sender, e);

    private void MessageBox_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        Conversation.MessageBox_OnTextChanged(sender, e);

    private void MessageBox_OnKeyDown(object? sender, KeyEventArgs e) =>
        Conversation.MessageBox_OnKeyDown(sender, e);

    private void SendButton_OnClick(object? sender, RoutedEventArgs e) =>
        Conversation.SendButton_OnClick(sender, e);

    private void ApproveButton_OnClick(object? sender, RoutedEventArgs e) =>
        Approval.ApproveButton_OnClick(sender, e);

    private void ConfirmApproveButton_OnClick(object? sender, RoutedEventArgs e) =>
        Approval.ConfirmApproveButton_OnClick(sender, e);

    private void ChoiceOptionButton_OnClick(object? sender, RoutedEventArgs e) =>
        Approval.ChoiceOptionButton_OnClick(sender, e);

    private void RetryButton_OnClick(object? sender, RoutedEventArgs e) =>
        Approval.RetryButton_OnClick(sender, e);

    private void RejectButton_OnClick(object? sender, RoutedEventArgs e) =>
        Approval.RejectButton_OnClick(sender, e);

    private void UsageStrip_OnClick(object? sender, RoutedEventArgs e) =>
        Conversation.UsageStrip_OnClick(sender, e);

    private void UsagePanelClose_OnClick(object? sender, RoutedEventArgs e) =>
        Conversation.UsagePanelClose_OnClick(sender, e);

    private void ViewChatButton_OnClick(object? sender, RoutedEventArgs e) =>
        Trajectory.ViewChatButton_OnClick(sender, e);

    private void ViewTraceButton_OnClick(object? sender, RoutedEventArgs e) =>
        Trajectory.ViewTraceButton_OnClick(sender, e);

    private void TrajectoryList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Trajectory.TrajectoryList_OnSelectionChanged(sender, e);

    protected override void OnClosed(EventArgs e)
    {
        Loc.Changed -= OnLocaleChanged;
        _pollTimer.Stop();
        _runBusyTimer.Stop();
        _client.Dispose();
        base.OnClosed(e);
    }
}
