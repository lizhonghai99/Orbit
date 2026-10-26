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

internal sealed class TrajectoryController
{
    private readonly MainWindow _host;

    public TrajectoryController(MainWindow host) => _host = host;

    internal void ViewChatButton_OnClick(object? sender, RoutedEventArgs e) => SetCenterView(trace: false);

    internal void ViewTraceButton_OnClick(object? sender, RoutedEventArgs e) => SetCenterView(trace: true);

    internal void SetCenterView(bool trace)
    {
        _host._traceView = trace;
        _host.ConversationScroll.IsVisible = !trace;
        _host.TrajectoryHost.IsVisible = trace;
        _host.ViewChatButton.Classes.Set("view-mode-active", !trace);
        _host.ViewTraceButton.Classes.Set("view-mode-active", trace);
        if (trace)
        {
            BindTrajectory(_host._lastDetail, scrollToEnd: true);
        }
    }

    internal void BindTrajectory(SessionDetail? detail, bool scrollToEnd)
    {
        var keepId = _host._selectedTrajectoryRecordId
                     ?? (_host.TrajectoryList.SelectedItem as TrajectoryRecordVm)?.RecordId;

        if (detail?.Entries is { Count: > 0 } entries)
        {
            _host._trajectoryRecords = TrajectoryProjector.Project(entries, _host._toolSchemaHints);
        }
        else
        {
            _host._trajectoryRecords = [];
        }

        _host.TrajectoryEmptyText.IsVisible = _host._trajectoryRecords.Count == 0;
        _host.TrajectoryList.ItemsSource = _host._trajectoryRecords;
        PaintTrajectoryTimeline(_host._trajectoryRecords);

        if (keepId is not null)
        {
            var match = _host._trajectoryRecords.FirstOrDefault(r =>
                string.Equals(r.RecordId, keepId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.CallId, keepId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                _host.TrajectoryList.SelectedItem = match;
            }
            else
            {
                ClearTraceDetail();
            }
        }
        else if (_host._trajectoryRecords.Count == 0)
        {
            ClearTraceDetail();
        }

        if (scrollToEnd && _host._traceView && _host._trajectoryRecords.Count > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _host.TrajectoryList.ScrollIntoView(_host._trajectoryRecords[^1]);
            }, DispatcherPriority.Background);
        }
    }

    internal void PaintTrajectoryTimeline(IReadOnlyList<TrajectoryRecordVm> records)
    {
        _host.TrajectoryTimelineBar.Children.Clear();
        _host.TrajectoryTimelineBar.ColumnDefinitions.Clear();
        var segments = TrajectoryProjector.BuildTimeline(records);
        if (segments.Count == 0)
        {
            return;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            _host.TrajectoryTimelineBar.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(Math.Max(1, seg.Weight), GridUnitType.Star)));
            var bar = new Border
            {
                Background = seg.FillBrush,
                Height = 10,
                CornerRadius = new CornerRadius(i == 0 ? 4 : 2, i == segments.Count - 1 ? 4 : 2, i == segments.Count - 1 ? 4 : 2, i == 0 ? 4 : 2),
                Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0)
            };
            Grid.SetColumn(bar, i);
            _host.TrajectoryTimelineBar.Children.Add(bar);
        }
    }

    internal void TrajectoryList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_host.TrajectoryList.SelectedItem is not TrajectoryRecordVm record)
        {
            ClearTraceDetail();
            return;
        }

        _host._selectedTrajectoryRecordId = record.RecordId;
        _host.TraceDetailPlaceholder.IsVisible = false;
        _host.TraceDetailTabs.IsVisible = true;
        _host.TraceOverviewLevel.Text = record.OverviewLevel;
        _host.TraceOverviewStatus.Text = record.OverviewStatus;
        _host.TraceParametersText.Text = string.IsNullOrWhiteSpace(record.ParametersText)
            ? Loc.T("traj.no_payload")
            : record.ParametersText;
        _host.TraceResultText.Text = string.IsNullOrWhiteSpace(record.ResultText)
            ? Loc.T("traj.no_result")
            : record.ResultText;
        _host.TraceSchemaText.Text = string.IsNullOrWhiteSpace(record.SchemaText)
            ? Loc.T("traj.schema_na")
            : record.SchemaText;
        _host.TraceTimingText.Text = string.IsNullOrWhiteSpace(record.TimingText)
            ? Loc.T("traj.timing.no_duration")
            : record.TimingText;
    }

    internal void ClearTraceDetail()
    {
        _host._selectedTrajectoryRecordId = null;
        _host.TraceDetailPlaceholder.IsVisible = true;
        _host.TraceDetailTabs.IsVisible = false;
        _host.TraceOverviewLevel.Text = "—";
        _host.TraceOverviewStatus.Text = "—";
        _host.TraceParametersText.Text = "—";
        _host.TraceResultText.Text = "—";
        _host.TraceSchemaText.Text = "—";
        _host.TraceTimingText.Text = "—";
    }
}
