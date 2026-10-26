using Avalonia.Controls;

namespace Orbit.Shell.I18n;

/// <summary>Applies <see cref="Loc"/> strings to named Shell controls (XAML stays English by default).</summary>
public static class UiText
{
    public static void Apply(MainWindow w)
    {
        Set(w, "HintDoubleClickAgent", Loc.T("app.hint.double_click_agent"));
        Tip(w, "SettingsButton", Loc.T("app.settings"));
        Tip(w, "WorkspaceChipButton", Loc.T("ws.tip"));
        Set(w, "AgentsSidebarTitle", Loc.T("app.agents"));
        SetButton(w, "NewChatButton", Loc.T("app.new_chat_cta"));
        Tip(w, "NewChatButton", Loc.T("app.new_chat_tip"));
        Set(w, "HistoryTitle", Loc.T("app.history"));
        w.Sessions.ApplyWorkspaceChipText();
        if (w.FindControl<CheckBox>("ShowArchivedCheck") is { } archived)
        {
            archived.Content = Loc.T("app.show_archived");
        }

        if (w.FindControl<MenuItem>("ArchiveSessionMenu") is { } archiveMenu)
        {
            archiveMenu.Header = Loc.T("app.archive_session");
        }

        if (w.FindControl<MenuItem>("DeleteSessionMenu") is { } deleteMenu)
        {
            deleteMenu.Header = Loc.T("app.delete_session");
        }

        if (w.FindControl<CheckBox>("ReasoningToggle") is { } reason)
        {
            reason.Content = Loc.T("app.reasoning");
            ToolTip.SetTip(reason, Loc.T("app.reasoning_tip"));
        }

        Tip(w, "ReasoningEffortBox", Loc.T("app.reasoning_effort_tip"));

        Set(w, "ConversationTitle", Loc.T("app.pick_session"));
        Set(w, "ConversationSubtitle", Loc.T("app.pick_session_sub"));
        SetButton(w, "ViewChatButton", Loc.T("traj.view.chat"));
        SetButton(w, "ViewTraceButton", Loc.T("traj.view.trace"));
        Set(w, "TrajectoryEmptyText", Loc.T("traj.empty"));
        Set(w, "TraceDetailPlaceholder", Loc.T("traj.pick_event"));
        Set(w, "TraceLabelLevel", Loc.T("traj.overview.level"));
        Set(w, "TraceLabelStatus", Loc.T("traj.overview.status"));
        if (w.FindControl<TabItem>("TraceTabOverview") is { } tabOverview)
        {
            tabOverview.Header = Loc.T("traj.tab.overview");
        }

        if (w.FindControl<TabItem>("TraceTabParameters") is { } tabParams)
        {
            tabParams.Header = Loc.T("traj.tab.parameters");
        }

        if (w.FindControl<TabItem>("TraceTabResult") is { } tabResult)
        {
            tabResult.Header = Loc.T("traj.tab.result");
        }

        if (w.FindControl<TabItem>("TraceTabSchema") is { } tabSchema)
        {
            tabSchema.Header = Loc.T("traj.tab.schema");
        }

        if (w.FindControl<TabItem>("TraceTabTiming") is { } tabTiming)
        {
            tabTiming.Header = Loc.T("traj.tab.timing");
        }

        Set(w, "StatusText", Loc.T("app.ready"));

        Set(w, "RunBusyLabel", Loc.T("app.running"));
        Set(w, "UsagePanelTitle", Loc.T("app.context_window"));
        Set(w, "UsagePanelSource", Loc.T("app.model_usage"));
        Set(w, "UsagePanelSubtitle", Loc.T("app.usage_subtitle"));
        Set(w, "UsagePanelHint", Loc.T("app.usage_hint"));
        Tip(w, "UsagePanelCloseButton", Loc.T("app.collapse"));

        if (w.FindControl<TextBox>("MessageBox") is { } box)
        {
            // Keep follow-up placeholder while a turn is in flight (SetRunBusy owns the busy string).
            var busy = w.FindControl<Border>("RunBusyBanner")?.IsVisible == true;
            box.PlaceholderText = Loc.T(busy ? "app.composer_followup" : "app.composer_placeholder");
        }

        // Keep UsagePanelTokens in sync when static labels refresh.
        if (w.FindControl<TextBlock>("UsagePanelSubtitle") is { } sub)
        {
            sub.Text = Loc.T("app.usage_subtitle");
        }

        Tip(w, "ComposerPlusButton", Loc.T("app.upload_tip"));
        Tip(w, "UsageStripButton", Loc.T("app.usage_tip"));
        Tip(w, "SendButton", Loc.T("status.send_tip"));
        SetButton(w, "SendButton", Loc.T("app.send_arrow"));

        // DataTemplate Copy buttons stay English in XAML; conversation rebinds on locale change.

        Set(w, "CapabilitiesTitle", Loc.T("app.capabilities"));
        Set(w, "LabelCurrentAgent", Loc.T("app.current_agent"));
        Set(w, "LabelModel", Loc.T("app.model"));
        Set(w, "LabelApprovals", Loc.T("app.approvals"));
        Set(w, "LabelHarness", Loc.T("app.harness"));
        Set(w, "HarnessStyleText", Loc.T("app.harness_hint"));
        Set(w, "LabelDomainTools", Loc.T("cap.tools_title", 0));
        Set(w, "LabelSkills", Loc.T("cap.skills_title", 0));
        Set(w, "LabelMcp", Loc.T("cap.mcp_title", 0));
        Set(w, "LabelReasoning", Loc.T("cap.reasoning"));
        Set(w, "McpPlaceholderText", Loc.T("cap.mcp_unconfigured"));
        Set(w, "ModelRestartHint", Loc.T("cap.requires_restart"));
        Set(w, "McpRestartHint", Loc.T("cap.requires_restart"));
        SetButton(w, "McpAddButton", Loc.T("cap.mcp_add"));
        Set(w, "McpRestartBarText", Loc.T("cap.mcp_restart_apply"));
        SetButton(w, "McpRestartNowButton", Loc.T("cap.mcp_restart_now"));
        Tip(w, "ToolsRefreshButton", Loc.T("cap.refresh_tip"));
        Tip(w, "SkillsRefreshButton", Loc.T("cap.refresh_tip"));
        Tip(w, "McpRefreshButton", Loc.T("cap.refresh_tip"));

        Set(w, "SettingsTitle", Loc.T("app.settings_llm"));
        SetButton(w, "CloseSettingsButton", Loc.T("app.close"));
        SetButton(w, "CancelSettingsButton", Loc.T("app.cancel"));
        SetButton(w, "SaveSettingsButton", Loc.T("app.save_apply"));
        Set(w, "SettingsEnvHint", Loc.T("app.settings_env_hint"));
        SetButton(w, "OpenLogsButton", Loc.T("app.open_logs"));
        Set(w, "LabelProvider", Loc.T("app.provider"));
        Set(w, "LabelProtocol", Loc.T("app.protocol"));
        Set(w, "LabelLanguage", Loc.T("app.language"));
        Set(w, "LabelEndpoint", Loc.T("app.endpoint"));
        Set(w, "LabelApiKey", Loc.T("app.api_key"));
        Set(w, "LabelDefaultModel", Loc.T("app.default_model"));
        SetButton(w, "SettingsRefreshModelsButton", Loc.T("app.fetch_models"));
        if (w.FindControl<TextBox>("SettingsApiKeyBox") is { } apiKeyBox)
        {
            apiKeyBox.PlaceholderText = Loc.T("app.api_key_placeholder");
        }

        Set(w, "McpOverlayTitle", Loc.T("cap.mcp_overlay_title"));
        Set(w, "McpOverlayHint", Loc.T("cap.mcp_overlay_hint"));
        Set(w, "LabelMcpId", Loc.T("cap.mcp_id"));
        Set(w, "LabelMcpTransport", Loc.T("cap.mcp_transport"));
        Set(w, "LabelMcpCommand", Loc.T("cap.mcp_command"));
        Set(w, "LabelMcpArgs", Loc.T("cap.mcp_args"));
        Set(w, "LabelMcpUrl", Loc.T("cap.mcp_url"));
        Set(w, "LabelMcpHeaders", Loc.T("cap.mcp_headers"));
        Set(w, "LabelMcpEnv", Loc.T("cap.mcp_env"));
        SetCheck(w, "McpTrustedCheck", Loc.T("cap.mcp_trusted_check"));
        SetCheck(w, "McpEnabledCheck", Loc.T("cap.mcp_enabled_check"));
        SetCheck(w, "McpRequiresApprovalCheck", Loc.T("cap.mcp_approval_check"));
        SetButton(w, "CloseMcpOverlayButton", Loc.T("app.close"));
        SetButton(w, "CancelMcpOverlayButton", Loc.T("app.cancel"));
        SetButton(w, "SaveMcpOverlayButton", Loc.T("cap.mcp_save"));
    }

    private static void SetCheck(MainWindow w, string name, string text)
    {
        if (w.FindControl<CheckBox>(name) is { } box)
        {
            box.Content = text;
        }
    }

    private static void Set(MainWindow w, string name, string text)
    {
        if (w.FindControl<TextBlock>(name) is { } tb)
        {
            tb.Text = text;
        }
    }

    private static void SetButton(MainWindow w, string name, string text)
    {
        if (w.FindControl<Button>(name) is { } b)
        {
            b.Content = text;
        }
    }

    private static void Tip(MainWindow w, string name, string tip)
    {
        if (w.FindControl<Control>(name) is { } c)
        {
            ToolTip.SetTip(c, tip);
        }
    }
}
