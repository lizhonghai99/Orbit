using System.Globalization;
using Avalonia.Data.Converters;
using Orbit.Contracts;

namespace Orbit.Shell.I18n;

/// <summary>Localize <see cref="AgentLifecycle"/> for the Agents list.</summary>
public sealed class LifecycleLabelConverter : IValueConverter
{
    public static readonly LifecycleLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AgentLifecycle life)
        {
            return Loc.T($"agent.lifecycle.{life}");
        }

        return value?.ToString() ?? "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
