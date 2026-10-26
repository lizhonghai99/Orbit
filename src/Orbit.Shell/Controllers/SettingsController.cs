using System.Diagnostics;
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

internal sealed class SettingsController
{
    private readonly MainWindow _host;

    public SettingsController(MainWindow host) => _host = host;

    internal void OpenSettingsButton_OnClick(object? sender, RoutedEventArgs e) => OpenSettingsPanel();

    internal void OpenLogsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = OrbitLocalAuth.LogsDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
            _host.SettingsStatusText.Text = Loc.T("status.logs_opened");
        }
        catch (Exception ex)
        {
            _host.SettingsStatusText.Text = Loc.T("status.logs_open_failed", ex.Message);
        }
    }

    internal void CloseSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        _host.SettingsOverlay.IsVisible = false;

    internal void SettingsDim_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _host.SettingsOverlay.IsVisible = false;

    internal void OpenSettingsPanel() =>
        UiAsync.Run(async () =>
        {
            await LoadSettingsFormAsync().ConfigureAwait(true);
            _host.SettingsOverlay.IsVisible = true;
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal async Task LoadSettingsFormAsync()
    {
        _host._settingsLoading = true;
        _host.SettingsLanguageBox.ItemsSource = new[]
        {
            Loc.T("app.language.en"),
            Loc.T("app.language.zh")
        };
        _host.SettingsLanguageBox.SelectedIndex = Loc.Current == UiLocale.Zh ? 1 : 0;

        await SyncLlmStatusAsync().ConfigureAwait(true);

        _host.SettingsProfileBox.Items.Clear();
        var status = LlmEnvStore.GetStatus();
        var activeId = status.ActiveProfileId ?? LlmEnvStore.Presets[0].Id;
        ComboBoxItem? activeItem = null;
        foreach (var preset in LlmEnvStore.Presets)
        {
            var item = new ComboBoxItem
            {
                Content = preset.DisplayName,
                Tag = preset.Id
            };
            _host.SettingsProfileBox.Items.Add(item);
            if (string.Equals(preset.Id, activeId, StringComparison.OrdinalIgnoreCase))
            {
                activeItem = item;
            }
        }

        _host.SettingsProfileBox.SelectedItem = activeItem ?? _host.SettingsProfileBox.Items[0];
        ApplySettingsProfile(CurrentSettingsPreset().Id);

        _host._settingsLoading = false;
        _host.SettingsStatusText.Text = status.HasApiKey
            ? Loc.T("status.key_detected", status.ActiveProfileId ?? "", status.Mode, status.Model ?? "")
            : Loc.T("status.key_missing");
    }

    internal void SettingsProfileBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_host._settingsLoading || _host.SettingsProfileBox.SelectedItem is null)
        {
            return;
        }

        var preset = CurrentSettingsPreset();
        ApplySettingsProfile(preset.Id);
        var profile = LlmEnvStore.GetStatus().Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase));
        if (profile is not { HasApiKey: true })
        {
            _host.SettingsStatusText.Text = Loc.T("status.provider_needs_key", preset.DisplayName);
        }
    }

    internal void ApplySettingsProfile(string profileId)
    {
        var preset = LlmEnvStore.Presets.First(p => p.Id == profileId);
        var status = LlmEnvStore.GetStatus();
        var profile = status.Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        _host.SettingsEndpointBox.Text = profile?.Endpoint ?? preset.DefaultEndpoint;
        var hasKey = profile?.HasApiKey == true;
        _host.SettingsApiKeyBox.Text = "";
        _host.SettingsApiKeyBox.PlaceholderText = hasKey
            ? Loc.T("app.api_key_keep")
            : Loc.T("app.api_key_enter");

        var activeId = status.ActiveProfileId;
        var model = string.Equals(activeId, profileId, StringComparison.OrdinalIgnoreCase)
            ? (status.Model ?? preset.DefaultModel)
            : preset.DefaultModel;
        _host.SettingsModelBox.ItemsSource = string.IsNullOrWhiteSpace(model) ? Array.Empty<string>() : new[] { model! };
        _host.SettingsModelBox.SelectedItem = model;
        if (_host.SettingsModelBox.SelectedItem is null && !string.IsNullOrWhiteSpace(model))
        {
            _host.SettingsModelBox.Text = model;
        }
    }

    internal void SettingsRefreshModelsButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            try
            {
                _host.SettingsModelsHint.Text = Loc.T("status.fetching_models");
                var preset = CurrentSettingsPreset();
                var key = _host.SettingsApiKeyBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = null; // Runtime uses stored key
                }

                var endpoint = _host.SettingsEndpointBox.Text?.Trim() ?? "";
                var models = await _host._client
                    .ListLlmModelsAsync(endpoint, key, preset.Id)
                    .ConfigureAwait(true);
                var previous = _host.SettingsModelBox.Text ?? _host.SettingsModelBox.SelectedItem as string;
                _host.SettingsModelBox.ItemsSource = models;
                if (!string.IsNullOrWhiteSpace(previous) &&
                    models.Contains(previous, StringComparer.OrdinalIgnoreCase))
                {
                    _host.SettingsModelBox.SelectedItem = models.First(m =>
                        string.Equals(m, previous, StringComparison.OrdinalIgnoreCase));
                }
                else if (models.Count > 0)
                {
                    _host.SettingsModelBox.SelectedIndex = 0;
                }

                _host.SettingsModelsHint.Text = Loc.T("status.models_count", models.Count);
            }
            catch (Exception ex)
            {
                _host.SettingsModelsHint.Text = Loc.T("status.failed");
                _host.SettingsStatusText.Text = ex.Message;
            }
        }, ex => _host.SettingsStatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void SaveSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            try
            {
                var newLocale = _host.SettingsLanguageBox.SelectedIndex == 1 ? UiLocale.Zh : UiLocale.En;
                var localeChanged = newLocale != Loc.Current;

                var preset = CurrentSettingsPreset();
                var model = (_host.SettingsModelBox.SelectedItem as string) ?? _host.SettingsModelBox.Text;
                var endpoint = _host.SettingsEndpointBox.Text?.Trim() ?? "";
                var apiKey = string.IsNullOrWhiteSpace(_host.SettingsApiKeyBox.Text)
                    ? null
                    : _host.SettingsApiKeyBox.Text.Trim();
                var prev = LlmEnvStore.GetStatus();
                var prevProfile = prev.ActiveProfileId;
                var prevModel = prev.Model;
                var prevEndpoint = prev.Profiles
                    .FirstOrDefault(p => string.Equals(p.Id, prevProfile, StringComparison.OrdinalIgnoreCase))
                    ?.Endpoint;

                var llmChanged =
                    !string.Equals(prevProfile, preset.Id, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(prevModel, model, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        prevEndpoint?.TrimEnd('/'),
                        endpoint.TrimEnd('/'),
                        StringComparison.OrdinalIgnoreCase)
                    || apiKey is not null;

                _host.SettingsOverlay.IsVisible = false;

                if (localeChanged)
                {
                    Loc.Set(newLocale);
                }

                if (!llmChanged)
                {
                    RefreshLlmUi();
                    _host.StatusText.Text = Loc.T("status.saved");
                    return;
                }

                _host.StatusText.Text = Loc.T("status.reloading_agents");
                var request = new SaveLlmProfileRequest
                {
                    ProfileId = preset.Id,
                    Endpoint = endpoint,
                    ApiKey = apiKey,
                    Protocol = LlmProtocol.Responses,
                    Model = model,
                    SetActive = true
                };
                var status = await _host._client.SaveLlmProfileAsync(request).ConfigureAwait(true);
                LlmEnvStore.CacheStatus(status);
                RefreshLlmUi();
                await RefreshModelListQuietAsync().ConfigureAwait(true);
                await _host.Sessions.ReloadRunningAgentsAsync().ConfigureAwait(true);
                _host.StatusText.Text = Loc.T("status.saved_reloaded");
            }
            catch (Exception ex)
            {
                _host.SettingsOverlay.IsVisible = true;
                _host.SettingsStatusText.Text = ex.Message;
            }
        }, ex =>
        {
            _host.SettingsOverlay.IsVisible = true;
            _host.SettingsStatusText.Text = ConversationController.Shorten(ex.Message, 120);
        });

    internal LlmProfileCatalog.LlmPreset CurrentSettingsPreset()
    {
        if (_host.SettingsProfileBox.SelectedItem is ComboBoxItem { Tag: string id })
        {
            return LlmEnvStore.Presets.FirstOrDefault(p =>
                       string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                   ?? LlmEnvStore.Presets[0];
        }

        return LlmEnvStore.Presets[0];
    }

    internal void RefreshReasoningUi()
    {
        _host._reasoningUiUpdating = true;
        try
        {
            _host.ReasoningToggle.IsChecked = LlmEnvStore.IsReasoningEnabled();
            var effort = LlmEnvStore.ResolveReasoningEffort();
            var items = LlmEnvStore.ReasoningEffortChoices
                .Select(id => new ComboBoxItem
                {
                    Content = Loc.T($"app.reasoning_effort.{id}"),
                    Tag = id
                })
                .ToList();
            _host.ReasoningEffortBox.ItemsSource = items;
            _host.ReasoningEffortBox.SelectedItem = items.FirstOrDefault(i =>
                string.Equals(i.Tag as string, effort, StringComparison.OrdinalIgnoreCase));
            _host.ReasoningEffortBox.IsEnabled = _host.ReasoningToggle.IsChecked == true;
        }
        finally
        {
            _host._reasoningUiUpdating = false;
        }
    }

    internal void ReasoningToggle_OnIsCheckedChanged(object? sender, RoutedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host._reasoningUiUpdating)
            {
                return;
            }

            var enabled = _host.ReasoningToggle.IsChecked == true;
            _host.ReasoningEffortBox.IsEnabled = enabled;
            var effort = (_host.ReasoningEffortBox.SelectedItem as ComboBoxItem)?.Tag as string
                         ?? LlmEnvStore.ResolveReasoningEffort();
            try
            {
                var status = await _host._client.SetLlmReasoningAsync(enabled, effort).ConfigureAwait(true);
                LlmEnvStore.CacheStatus(status);
                LlmEnvStore.CacheReasoning(enabled, effort);
                await _host.Sessions.ReloadRunningAgentsAsync().ConfigureAwait(true);
                _host.StatusText.Text = Loc.T(
                    "status.reasoning_updated",
                    enabled ? Loc.T("app.reasoning") : "off");
            }
            catch (Exception ex)
            {
                _host.StatusText.Text = ex.Message;
            }
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void ReasoningEffortBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host._reasoningUiUpdating || _host.ReasoningToggle.IsChecked != true)
            {
                return;
            }

            if (_host.ReasoningEffortBox.SelectedItem is not ComboBoxItem { Tag: string effort })
            {
                return;
            }

            try
            {
                var status = await _host._client.SetLlmReasoningAsync(true, effort).ConfigureAwait(true);
                LlmEnvStore.CacheStatus(status);
                LlmEnvStore.CacheReasoning(true, effort);
                await _host.Sessions.ReloadRunningAgentsAsync().ConfigureAwait(true);
                _host.StatusText.Text = Loc.T("status.reasoning_updated", Loc.T($"app.reasoning_effort.{effort}"));
            }
            catch (Exception ex)
            {
                _host.StatusText.Text = ex.Message;
            }
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void ModelBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UiAsync.Run(async () =>
        {
            if (_host._modelBoxUpdating)
            {
                return;
            }

            var model = (_host.ModelBox.SelectedItem as string) ?? _host.ModelBox.Text;
            if (string.IsNullOrWhiteSpace(model))
            {
                return;
            }

            try
            {
                var status = await _host._client.SetLlmModelAsync(model).ConfigureAwait(true);
                LlmEnvStore.CacheStatus(status);
                RefreshLlmUi();
                await _host.Sessions.ReloadRunningAgentsAsync().ConfigureAwait(true);
                _host.StatusText.Text = Loc.T("status.model_switched", model);
            }
            catch (Exception ex)
            {
                _host.StatusText.Text = ex.Message;
            }
        }, ex => _host.StatusText.Text = ConversationController.Shorten(ex.Message, 120));

    internal void RefreshLlmUi()
    {
        var status = LlmEnvStore.GetStatus();
        if (status.Mode == "responses")
        {
            _host.LlmStatusText.Text = Loc.T("llm.status_responses", status.Model ?? "");
            var name = LlmEnvStore.Presets
                           .FirstOrDefault(p => string.Equals(p.Id, status.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                           ?.DisplayName.Split('/')[0].Trim()
                       ?? status.ActiveProfileId
                       ?? "LLM";
            _host.LlmBadgeText.Text = Loc.T("llm.badge_named", name);
            _host.LlmBadgeText.Foreground = new SolidColorBrush(Color.Parse("#166534"));
            _host.ContextLlmText.Text = $"{status.ActiveProfileId}\n{status.Model}\n{status.EndpointHost}";
        }
        else
        {
            _host.LlmStatusText.Text = Loc.T("llm.missing_key");
            _host.LlmBadgeText.Text = Loc.T("llm.badge_missing");
            _host.LlmBadgeText.Foreground = new SolidColorBrush(Color.Parse("#B42318"));
            _host.ContextLlmText.Text = Loc.T("llm.context_missing");
        }

        _host._modelBoxUpdating = true;
        try
        {
            var model = status.Model;
            if (!string.IsNullOrWhiteSpace(model))
            {
                var items = _host.ModelBox.ItemsSource as IList<string> ?? [];
                if (!items.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    _host.ModelBox.ItemsSource = items.Append(model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }

                _host.ModelBox.SelectedItem = (_host.ModelBox.ItemsSource as IEnumerable<string>)
                    ?.FirstOrDefault(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
                if (_host.ModelBox.SelectedItem is null)
                {
                    _host.ModelBox.Text = model;
                }
            }
        }
        finally
        {
            _host._modelBoxUpdating = false;
        }

        _host.Conversation.RefreshContextUsageUi();
    }

    internal async Task RefreshModelListQuietAsync()
    {
        try
        {
            var status = LlmEnvStore.GetStatus();
            var profile = status.Profiles.FirstOrDefault(p => p.IsActive);
            if (profile is null || string.IsNullOrWhiteSpace(profile.Endpoint) || !status.HasApiKey)
            {
                return;
            }

            var models = await _host._client
                .ListLlmModelsAsync(profile.Endpoint!, profileId: profile.Id)
                .ConfigureAwait(true);
            var current = status.Model;
            _host._modelBoxUpdating = true;
            try
            {
                _host.ModelBox.ItemsSource = models;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    _host.ModelBox.SelectedItem = models.FirstOrDefault(m =>
                        string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
                    if (_host.ModelBox.SelectedItem is null)
                    {
                        _host.ModelBox.Text = current;
                    }
                }
            }
            finally
            {
                _host._modelBoxUpdating = false;
            }
        }
        catch
        {
            // keep quiet; settings dialog shows errors
        }
    }

    internal async Task SyncLlmStatusAsync()
    {
        try
        {
            var status = await _host._client.GetLlmStatusAsync().ConfigureAwait(true);
            if (status is not null)
            {
                LlmEnvStore.CacheStatus(status);
            }
        }
        catch
        {
            // Runtime offline — keep cached / empty status
        }
    }
}
