using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orbit.Contracts;
using Orbit.Shell.I18n;

namespace Orbit.Shell.Controllers;

internal sealed class CapabilitiesController
{
    private readonly MainWindow _host;
    private readonly SemaphoreSlim _capGate = new(1, 1);
    private readonly SemaphoreSlim _mcpSaveGate = new(1, 1);
    private AgentCapabilitiesDto? _lastOk;
    private bool _agentRunning;
    private bool _suppressToggle;
    private bool _mcpNeedsRestart;

    public CapabilitiesController(MainWindow host) => _host = host;

    internal async Task RefreshAsync()
    {
        if (_host.AgentsList.SelectedItem is not AgentInfo agent)
        {
            ShowNeedAgent();
            return;
        }

        if (agent.Lifecycle != AgentLifecycle.Running)
        {
            ShowOffline(agent.Id, loaded: false);
            return;
        }

        try
        {
            var caps = await _host._client.GetAgentCapabilitiesAsync(agent.Id).ConfigureAwait(true);
            if (caps is null)
            {
                _host.StatusText.Text = Loc.T("cap.fetch_failed", "null");
                return;
            }

            if (caps.Offline)
            {
                ShowOffline(agent.Id, loaded: true);
                return;
            }

            _lastOk = caps;
            _agentRunning = true;
            Bind(caps);
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = Loc.T("cap.fetch_failed", ConversationController.Shorten(ex.Message, 80));
            // Keep last successful panel contents.
        }
    }

    internal void ShowNeedAgent()
    {
        _agentRunning = false;
        _host.CapabilitiesStatusText.Text = Loc.T("cap.need_agent");
        _host.CapabilitiesStatusText.IsVisible = true;
        _host.CapabilitiesAppliedHint.IsVisible = false;
        ClearLists();
        _host.HarnessModulesPanel.Children.Clear();
        _host.HarnessStyleText.Text = Loc.T("harness.pick_agent");
        _host.ContextLlmText.Text = "—";
        _host.ContextReasoningText.Text = "—";
        SetSectionCounts(0, 0, 0);
        _host.ContextToolsText.Text = "—";
        _host.ContextSkillsText.Text = "—";
        _host._lastSkillsChars = 0;
    }

    internal void ShowOffline(string agentId, bool loaded)
    {
        _agentRunning = false;
        _host.CapabilitiesStatusText.Text = Loc.T("cap.offline");
        _host.CapabilitiesStatusText.IsVisible = true;
        _host.CapabilitiesAppliedHint.IsVisible = false;
        ClearLists();
        _host.HarnessModulesPanel.Children.Clear();
        _host.HarnessStyleText.Text = Loc.T("harness.load_agent");
        _host.ContextLlmText.Text = "—";
        _host.ContextReasoningText.Text = "—";
        SetSectionCounts(0, 0, 0);
        _host.ContextToolsText.Text = Loc.T("harness.not_running");
        _host.ContextSkillsText.Text = "—";
        _host._lastSkillsChars = 0;
        _ = agentId;
        _ = loaded;
    }

    private void Bind(AgentCapabilitiesDto caps)
    {
        _host.CapabilitiesStatusText.IsVisible = false;
        _host.ContextLlmText.Text = string.IsNullOrWhiteSpace(caps.Model) ? "—" : caps.Model!;
        _host.ContextReasoningText.Text = string.IsNullOrWhiteSpace(caps.Reasoning) ? "default" : caps.Reasoning!;
        _host.ModelRestartHint.Text = Loc.T("cap.requires_restart");
        _host.ModelRestartHint.IsVisible = true;
        _host.McpRestartHint.Text = Loc.T("cap.requires_restart");
        _host.McpRestartHint.IsVisible = true;
        UpdateMcpRestartBar();

        BindHarness(caps.Harness);
        BindTools(caps.Tools);
        BindSkills(caps.Skills);
        BindMcp(caps.Mcp);

        _host.ContextToolsText.Text = caps.Tools.Count == 0
            ? Loc.T("harness.tools_none")
            : string.Join('\n', caps.Tools.Select(t => t.Name));
        _host.ContextSkillsText.Text = caps.Skills.Count == 0
            ? Loc.T("harness.skills_none")
            : string.Join('\n', caps.Skills.Select(s => s.Name));
        _host._lastSkillsChars = caps.Skills.Sum(s =>
            (s.Name?.Length ?? 0) + (s.Description?.Length ?? 0) + 400);

        _host._toolSchemaHints = caps.Tools
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .ToDictionary(
                t => t.Name,
                t => t.Description ?? t.Name,
                StringComparer.OrdinalIgnoreCase);
    }

    private void BindHarness(AgentHarnessDto? harness)
    {
        _host.HarnessModulesPanel.Children.Clear();
        if (harness is null)
        {
            _host.HarnessStyleText.Text = Loc.T("harness.unavailable");
            return;
        }

        _host.HarnessStyleText.Text = ConversationController.FriendHarnessStyle(harness.Style);
        var muted = ConversationResolveBrush("OrbitMuted", "#6B6B70");
        var subtle = ConversationResolveBrush("OrbitSubtle", "#8E8E93");

        foreach (var module in harness.Modules)
        {
            var (titleZh, detailZh) = ConversationController.FriendHarnessModule(module);
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = $"{(module.Enabled ? "●" : "○")} {titleZh}",
                FontSize = 12,
                FontWeight = FontWeight.SemiBold
            });
            row.Children.Add(new TextBlock
            {
                Text = detailZh,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = module.Enabled ? muted : subtle
            });
            _host.HarnessModulesPanel.Children.Add(row);
        }
    }

    private void BindTools(IReadOnlyList<CapabilityToolDto> tools)
    {
        _host.ToolsListPanel.Children.Clear();
        SetToolsTitle(tools.Count);
        var muted = ConversationResolveBrush("OrbitMuted", "#6B6B70");

        foreach (var tool in tools)
        {
            var badges = new List<(string Text, IBrush Fg, IBrush Bg, IBrush Bd)>();
            if (tool.RequiresApproval)
            {
                badges.Add((
                    Loc.T("cap.badge_approval"),
                    ConversationResolveBrush("OrbitApprovalBorder", "#B45309"),
                    ConversationResolveBrush("OrbitApprovalBg", "#FEF3C7"),
                    ConversationResolveBrush("OrbitApprovalBorder", "#F59E0B")));
            }

            if (string.Equals(tool.Source, "harness", StringComparison.OrdinalIgnoreCase))
            {
                badges.Add((Loc.T("cap.badge_harness"), muted, ConversationResolveBrush("OrbitChipBg", "#EAEAEC"), ConversationResolveBrush("OrbitBorder", "#D0D0D4")));
            }

            var name = tool.Name;
            _host.ToolsListPanel.Children.Add(BuildRow(
                tool.Name,
                tool.Description,
                tip: $"{tool.Name}\n{tool.Description}",
                badges,
                muted,
                enabled: tool.Enabled,
                onToggle: isOn => _ = PatchToolAsync(name, isOn)));
        }
    }

    private void BindSkills(IReadOnlyList<CapabilitySkillDto> skills)
    {
        _host.SkillsListPanel.Children.Clear();
        SetSkillsTitle(skills.Count);
        var muted = ConversationResolveBrush("OrbitMuted", "#6B6B70");

        foreach (var skill in skills)
        {
            var badges = new List<(string Text, IBrush Fg, IBrush Bg, IBrush Bd)>();
            badges.Add((
                string.Equals(skill.Source, "user", StringComparison.OrdinalIgnoreCase)
                    ? Loc.T("cap.badge_user")
                    : Loc.T("cap.badge_builtin"),
                muted,
                ConversationResolveBrush("OrbitChipBg", "#EAEAEC"),
                ConversationResolveBrush("OrbitBorder", "#D0D0D4")));

            if (skill.HasScripts)
            {
                badges.Add((
                    Loc.T("cap.badge_scripts"),
                    new SolidColorBrush(Color.Parse("#9A3412")),
                    new SolidColorBrush(Color.Parse("#FFEDD5")),
                    new SolidColorBrush(Color.Parse("#F97316"))));
            }

            var sha = string.IsNullOrWhiteSpace(skill.Sha256) || skill.Sha256.Length < 8
                ? ""
                : skill.Sha256[..8];
            var title = string.IsNullOrWhiteSpace(sha) ? skill.Name : $"{skill.Name} · {sha}";
            var skillId = skill.Id;
            _host.SkillsListPanel.Children.Add(BuildRow(
                title,
                skill.Description,
                tip: $"{skill.Id}\n{skill.Name}\n{skill.Description}\n{skill.Path}",
                badges,
                muted,
                enabled: skill.Enabled,
                onToggle: isOn => _ = PatchSkillAsync(skillId, isOn)));
        }
    }

    private void BindMcp(IReadOnlyList<CapabilityMcpServerDto> mcp)
    {
        _suppressToggle = true;
        try
        {
            _host.McpListPanel.Children.Clear();
            SetMcpTitle(mcp.Count);
            if (mcp.Count == 0)
            {
                _host.McpPlaceholderText.Text = Loc.T("cap.mcp_unconfigured");
                _host.McpPlaceholderText.IsVisible = true;
                return;
            }

            _host.McpPlaceholderText.IsVisible = false;
            var muted = ConversationResolveBrush("OrbitMuted", "#6B6B70");

            foreach (var m in mcp)
            {
                _host.McpListPanel.Children.Add(BuildMcpRow(m, muted));
            }
        }
        finally
        {
            _suppressToggle = false;
        }
    }

    private Control BuildMcpRow(CapabilityMcpServerDto m, IBrush muted)
    {
        var root = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        var tip = string.IsNullOrWhiteSpace(m.Error)
            ? $"{m.Id}\n{m.Transport}\n{m.Status}"
            : $"{m.Id}\n{m.Status}\n{m.Error}";
        ToolTip.SetTip(root, tip);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        head.Children.Add(new TextBlock
        {
            Text = $"{m.Id} · {m.Transport ?? "?"} · {m.Status} · {Loc.T("cap.mcp_tools_count", m.ToolCount)}",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            VerticalAlignment = VerticalAlignment.Center
        });

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(badges, 1);
        if (!m.Trusted)
        {
            badges.Children.Add(MakeBadge(
                Loc.T("cap.mcp_untrusted"),
                new SolidColorBrush(Color.Parse("#B45309")),
                new SolidColorBrush(Color.Parse("#FEF3C7")),
                new SolidColorBrush(Color.Parse("#F59E0B"))));
        }

        if (string.Equals(m.Status, "error", StringComparison.OrdinalIgnoreCase))
        {
            badges.Children.Add(MakeBadge(
                Loc.T("cap.mcp_error"),
                new SolidColorBrush(Color.Parse("#B42318")),
                new SolidColorBrush(Color.Parse("#FEE4E2")),
                new SolidColorBrush(Color.Parse("#F97066"))));
        }

        head.Children.Add(badges);

        var trust = new ToggleSwitch
        {
            IsChecked = m.Trusted,
            IsEnabled = _agentRunning,
            MinHeight = 18,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            OnContent = null,
            OffContent = null
        };
        ToolTip.SetTip(trust, Loc.T("cap.mcp_trust_tip"));
        Grid.SetColumn(trust, 2);
        var serverId = m.Id;
        trust.IsCheckedChanged += (_, _) =>
        {
            if (_suppressToggle || trust.IsChecked is not bool isOn)
            {
                return;
            }

            _ = SetTrustAsync(serverId, isOn);
        };
        head.Children.Add(trust);

        var del = new Button
        {
            Content = "✕",
            Classes = { "ghost" },
            Padding = new Avalonia.Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(del, Loc.T("cap.mcp_delete_tip"));
        Grid.SetColumn(del, 3);
        del.Click += (_, _) => _ = DeleteServerAsync(serverId);
        head.Children.Add(del);

        root.Children.Add(head);
        if (!m.Enabled)
        {
            root.Children.Add(new TextBlock
            {
                Text = Loc.T("cap.mcp_disabled"),
                FontSize = 11,
                Foreground = muted
            });
        }

        return root;
    }

    private static Border MakeBadge(string text, IBrush fg, IBrush bg, IBrush bd) =>
        new()
        {
            Background = bg,
            BorderBrush = bd,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(5, 1),
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = fg }
        };

    internal void OpenAddMcpOverlay()
    {
        if (_host.AgentsList.SelectedItem is not AgentInfo)
        {
            _host.StatusText.Text = Loc.T("cap.need_agent");
            return;
        }

        _host.McpIdBox.Text = "";
        _host.McpCommandBox.Text = "";
        _host.McpArgsBox.Text = "";
        _host.McpUrlBox.Text = "";
        _host.McpHeadersBox.Text = "";
        _host.McpEnvBox.Text = "";
        _host.McpTrustedCheck.IsChecked = false;
        _host.McpEnabledCheck.IsChecked = true;
        _host.McpRequiresApprovalCheck.IsChecked = false;
        _host.McpOverlayStatusText.Text = "";
        _host.McpTransportBox.SelectedIndex = 0;
        SyncMcpTransportFields();
        _host.McpOverlay.IsVisible = true;
    }

    internal void CloseAddMcpOverlay() => _host.McpOverlay.IsVisible = false;

    internal void SyncMcpTransportFields()
    {
        // Named panels may still be null if SelectionChanged fired mid-InitializeComponent.
        if (_host.McpStdioFields is null || _host.McpHttpFields is null)
        {
            return;
        }

        var transport = SelectedTransport();
        var http = string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase);
        _host.McpStdioFields.IsVisible = !http;
        _host.McpHttpFields.IsVisible = http;
    }

    internal async Task SaveMcpOverlayAsync()
    {
        if (_host.AgentsList.SelectedItem is not AgentInfo agent)
        {
            _host.McpOverlayStatusText.Text = Loc.T("cap.need_agent");
            return;
        }

        await _mcpSaveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var server = BuildServerFromOverlay();
            // Re-GET under the gate so a prior save's revision is visible before PUT.
            var current = await _host._client.GetMcpConfigAsync(agent.Id).ConfigureAwait(true);
            current.Servers ??= [];
            current.Servers.RemoveAll(s =>
                string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase));
            current.Servers.Add(server);

            var result = await _host._client.PutMcpConfigAsync(agent.Id, current).ConfigureAwait(true);
            MaskPlainSecretsInBox(_host.McpHeadersBox, server.Headers);
            _mcpNeedsRestart = result.RequiresRestart;
            UpdateMcpRestartBar();
            CloseAddMcpOverlay();
            _host.StatusText.Text = Loc.T("cap.mcp_saved");
            if (result.Warnings.Count > 0)
            {
                _host.StatusText.Text = Loc.T("cap.mcp_saved_warn", result.Warnings[0]);
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var msg = ConversationController.Shorten(ex.Message, 160);
            _host.McpOverlayStatusText.Text = msg.Contains("mcp_config_stale", StringComparison.OrdinalIgnoreCase)
                ? Loc.T("cap.mcp_stale")
                : Loc.T("cap.mcp_save_failed", msg);
        }
        finally
        {
            _mcpSaveGate.Release();
        }
    }

    internal async Task RestartAgentNowAsync()
    {
        if (_host.AgentsList.SelectedItem is not AgentInfo agent)
        {
            return;
        }

        try
        {
            _host.StatusText.Text = Loc.T("cap.mcp_restarting");
            await _host._client.RestartAgentAsync(agent.Id).ConfigureAwait(true);
            _mcpNeedsRestart = false;
            UpdateMcpRestartBar();
            await _host.Sessions.RefreshAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            _host.StatusText.Text = Loc.T("cap.mcp_restarted");
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = Loc.T("cap.mcp_restart_failed", ConversationController.Shorten(ex.Message, 120));
        }
    }

    private async Task SetTrustAsync(string serverId, bool trusted)
    {
        if (_host.AgentsList.SelectedItem is not AgentInfo agent)
        {
            return;
        }

        try
        {
            var result = await _host._client.SetMcpTrustAsync(agent.Id, serverId, trusted).ConfigureAwait(true);
            _mcpNeedsRestart = result.RequiresRestart;
            UpdateMcpRestartBar();
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = Loc.T("cap.mcp_trust_failed", ConversationController.Shorten(ex.Message, 100));
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task DeleteServerAsync(string serverId)
    {
        if (_host.AgentsList.SelectedItem is not AgentInfo agent)
        {
            return;
        }

        var confirm = new Window
        {
            Title = Loc.T("cap.mcp_delete_title"),
            Width = 380,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = SessionListController.BuildConfirmPanel(
                Loc.T("cap.mcp_delete_confirm", serverId),
                out var ok,
                out var cancel)
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
            var result = await _host._client.DeleteMcpServerAsync(agent.Id, serverId).ConfigureAwait(true);
            _mcpNeedsRestart = result.RequiresRestart;
            UpdateMcpRestartBar();
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = Loc.T("cap.mcp_delete_failed", ConversationController.Shorten(ex.Message, 100));
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private McpServerConfig BuildServerFromOverlay()
    {
        var id = (_host.McpIdBox.Text ?? "").Trim().ToLowerInvariant();
        var transport = SelectedTransport();
        var env = ParseKvLines(_host.McpEnvBox.Text, out var envBad);
        var headers = ParseKvLines(_host.McpHeadersBox.Text, out var headerBad);
        if (envBad.Count > 0 || headerBad.Count > 0)
        {
            var sample = envBad.Concat(headerBad).First();
            throw new ArgumentException(
                Loc.T("cap.mcp_kv_invalid", ConversationController.Shorten(sample, 60)));
        }

        return new McpServerConfig
        {
            Id = id,
            Transport = transport,
            Command = string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase)
                ? (_host.McpCommandBox.Text ?? "").Trim()
                : null,
            Args = string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase)
                ? ParseLines(_host.McpArgsBox.Text)
                : [],
            Url = string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase)
                ? (_host.McpUrlBox.Text ?? "").Trim()
                : null,
            Headers = headers,
            Env = env,
            Trusted = _host.McpTrustedCheck.IsChecked == true,
            Enabled = _host.McpEnabledCheck.IsChecked != false,
            RequiresApproval = _host.McpRequiresApprovalCheck.IsChecked == true,
            ToolPrefix = "",
            TimeoutSeconds = 30
        };
    }

    private string SelectedTransport()
    {
        if (_host.McpTransportBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            return tag;
        }

        if (_host.McpTransportBox.SelectedItem is ComboBoxItem item)
        {
            return (item.Content?.ToString() ?? "stdio").Trim().ToLowerInvariant();
        }

        return "stdio";
    }

    private static List<string> ParseLines(string? text) =>
        (text ?? "")
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => l.Length > 0)
        .ToList();

    /// <summary>
    /// Accepts <c>KEY=value</c> or HTTP-style <c>Key: value</c> (first <c>=</c>, else first <c>:</c>).
    /// </summary>
    private static Dictionary<string, string> ParseKvLines(string? text, out List<string> invalidLines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        invalidLines = [];
        foreach (var line in ParseLines(text))
        {
            var sep = line.IndexOf('=');
            if (sep <= 0)
            {
                sep = line.IndexOf(':');
            }

            if (sep <= 0)
            {
                invalidLines.Add(line);
                continue;
            }

            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            if (key.Length == 0)
            {
                invalidLines.Add(line);
                continue;
            }

            map[key] = value;
        }

        return map;
    }

    private static void MaskPlainSecretsInBox(TextBox box, Dictionary<string, string> headers)
    {
        var lines = new List<string>();
        foreach (var (key, value) in headers)
        {
            if (value.Length > 40 && !value.Contains("${", StringComparison.Ordinal))
            {
                lines.Add($"{key}=••••");
            }
            else
            {
                lines.Add($"{key}={value}");
            }
        }

        box.Text = string.Join('\n', lines);
    }

    private void UpdateMcpRestartBar()
    {
        _host.McpRestartBar.IsVisible = _mcpNeedsRestart;
        _host.McpRestartBarText.Text = Loc.T("cap.mcp_restart_apply");
        _host.McpRestartNowButton.Content = Loc.T("cap.mcp_restart_now");
    }

    private async Task PatchToolAsync(string toolName, bool enabled)
    {
        await _capGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!_agentRunning || _lastOk is null || _host.AgentsList.SelectedItem is not AgentInfo agent)
            {
                return;
            }

            // Only patch tools — leave skills null so Runtime keeps disk truth (avoids clobber).
            var disabledTools = _lastOk.Tools
                .Where(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase) ? !enabled : !t.Enabled)
                .Select(t => t.Name)
                .ToArray();
            await SendPatchAsync(agent.Id, disabledSkills: null, disabledTools).ConfigureAwait(true);
        }
        finally
        {
            _capGate.Release();
        }
    }

    private async Task PatchSkillAsync(string skillId, bool enabled)
    {
        await _capGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!_agentRunning || _lastOk is null || _host.AgentsList.SelectedItem is not AgentInfo agent)
            {
                return;
            }

            var disabledSkills = _lastOk.Skills
                .Where(s => string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase) ? !enabled : !s.Enabled)
                .Select(s => s.Id)
                .ToArray();
            await SendPatchAsync(agent.Id, disabledSkills, disabledTools: null).ConfigureAwait(true);
        }
        finally
        {
            _capGate.Release();
        }
    }

    private async Task SendPatchAsync(
        string agentId,
        IReadOnlyList<string>? disabledSkills,
        IReadOnlyList<string>? disabledTools)
    {
        try
        {
            var result = await _host._client.PatchAgentCapabilitiesAsync(
                agentId,
                new CapabilityPatchRequest
                {
                    DisabledSkills = disabledSkills,
                    DisabledTools = disabledTools
                }).ConfigureAwait(true);

            _host.CapabilitiesAppliedHint.Text = Loc.T(
                "cap.applied_hint",
                result.Applied.Count,
                result.AffectedSessions);
            _host.CapabilitiesAppliedHint.IsVisible = true;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.StatusText.Text = Loc.T("cap.patch_failed", ConversationController.Shorten(ex.Message, 100));
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private Control BuildRow(
        string name,
        string? description,
        string tip,
        List<(string Text, IBrush Fg, IBrush Bg, IBrush Bd)> badges,
        IBrush muted,
        bool enabled,
        Action<bool> onToggle)
    {
        // Compact row: name | badges | switch. Description stays in the tooltip.
        var root = new Border
        {
            Padding = new Avalonia.Thickness(4, 4),
            CornerRadius = new Avalonia.CornerRadius(8),
            Margin = new Avalonia.Thickness(0, 0, 0, 1)
        };
        ToolTip.SetTip(root, tip);

        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 6
        };
        head.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (badges.Count > 0)
        {
            var badgeRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center
            };
            foreach (var b in badges.Take(2))
            {
                badgeRow.Children.Add(new Border
                {
                    Background = b.Bg,
                    BorderBrush = b.Bd,
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(4),
                    Padding = new Avalonia.Thickness(4, 0),
                    Child = new TextBlock
                    {
                        Text = b.Text,
                        FontSize = 9,
                        Foreground = b.Fg
                    }
                });
            }

            Grid.SetColumn(badgeRow, 1);
            head.Children.Add(badgeRow);
        }

        var toggle = new ToggleSwitch
        {
            IsChecked = enabled,
            IsEnabled = _agentRunning,
            MinHeight = 18,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            OnContent = null,
            OffContent = null
        };
        if (!_agentRunning)
        {
            ToolTip.SetTip(toggle, Loc.T("cap.toggle_need_running"));
        }

        Grid.SetColumn(toggle, 2);
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (_suppressToggle || toggle.IsChecked is not bool isOn)
            {
                return;
            }

            onToggle(isOn);
        };
        head.Children.Add(toggle);
        root.Child = head;
        _ = muted;
        return root;
    }

    private void ClearLists()
    {
        _suppressToggle = true;
        try
        {
            _host.ToolsListPanel.Children.Clear();
            _host.SkillsListPanel.Children.Clear();
        }
        finally
        {
            _suppressToggle = false;
        }

        _host.McpListPanel.Children.Clear();
        _host.McpPlaceholderText.Text = Loc.T("cap.mcp_unconfigured");
        _host.McpPlaceholderText.IsVisible = true;
        _host.ModelRestartHint.IsVisible = false;
        _host.McpRestartHint.IsVisible = false;
        _mcpNeedsRestart = false;
        UpdateMcpRestartBar();
    }

    private void SetSectionCounts(int tools, int skills, int mcp)
    {
        SetToolsTitle(tools);
        SetSkillsTitle(skills);
        SetMcpTitle(mcp);
    }

    private void SetToolsTitle(int n) =>
        _host.LabelDomainTools.Text = Loc.T("cap.tools_title", n);

    private void SetSkillsTitle(int n) =>
        _host.LabelSkills.Text = Loc.T("cap.skills_title", n);

    private void SetMcpTitle(int n) =>
        _host.LabelMcp.Text = Loc.T("cap.mcp_title", n);

    private IBrush ConversationResolveBrush(string key, string fallback) =>
        _host.Conversation.ResolveBrush(key, fallback);
}
