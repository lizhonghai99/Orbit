using Avalonia.Media;

namespace Orbit.Shell.ViewModels;

public enum TrajectoryKind
{
    User,
    Assistant,
    Thinking,
    Tool,
    Approval,
    Meta,
    Error,
    Context
}

/// <summary>One row in the Trajectory ledger (DeepSeek-style; projected from Session JSONL).</summary>
public sealed class TrajectoryRecordVm
{
    public required string RecordId { get; init; }
    public required TrajectoryKind Kind { get; init; }
    public required string KindLabel { get; init; }
    /// <summary>Primary line (tool name or short role title).</summary>
    public required string Title { get; init; }
    /// <summary>Secondary preview (args → result, or message excerpt).</summary>
    public required string Subtitle { get; init; }
    public required string Summary { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public long? DurationMs { get; init; }

    public TimeSpan? Duration =>
        DurationMs is { } ms
            ? TimeSpan.FromMilliseconds(ms)
            : EndedAt is { } end && end > StartedAt
                ? end - StartedAt
                : null;

    public string DurationLabel
    {
        get
        {
            var d = Duration;
            if (d is null)
            {
                return "";
            }

            return d.Value.TotalSeconds >= 1
                ? $"{d.Value.TotalSeconds:0.##}s"
                : $"{Math.Max(0, (int)d.Value.TotalMilliseconds)}ms";
        }
    }

    public bool HasDuration => !string.IsNullOrEmpty(DurationLabel);

    public string? ToolName { get; init; }
    public string? CallId { get; init; }

    public required string OverviewLevel { get; init; }
    public required string OverviewStatus { get; init; }

    public string ParametersText { get; init; } = "";
    public string ResultText { get; init; } = "";
    public string SchemaText { get; init; } = "";
    public string TimingText { get; init; } = "";

    public string ColorHex { get; init; } = "#8E8E93";
    public string SoftColorHex { get; init; } = "#F0F0F1";

    public IBrush KindBrush => new SolidColorBrush(Color.Parse(ColorHex));
    public IBrush SoftBrush => new SolidColorBrush(Color.Parse(SoftColorHex));

    public double TimelineWeight { get; init; } = 1;
}

public sealed class TrajectoryTimelineSegmentVm
{
    public required string ColorHex { get; init; }
    public required double Weight { get; init; }
    public IBrush FillBrush => new SolidColorBrush(Color.Parse(ColorHex));
}
