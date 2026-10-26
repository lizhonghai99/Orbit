using Avalonia.Media;

namespace Orbit.Shell.ViewModels;

public sealed class ContextUsageRowVm
{
    public required string Label { get; init; }
    public required string TokensText { get; init; }
    public required IBrush ColorBrush { get; init; }
}
