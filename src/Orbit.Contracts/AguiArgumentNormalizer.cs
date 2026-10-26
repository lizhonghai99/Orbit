using System.Globalization;
using System.Text.Json;

namespace Orbit.Contracts;

/// <summary>
/// Single source of truth for AG-UI-safe tool argument values.
/// AGUIJsonSerializerContext cannot serialize boxed <see cref="double"/> / <see cref="long"/> in object graphs.
/// Numbers become invariant strings (e.g. <c>"20"</c>); MEAI still binds them back to <c>int</c> parameters on invoke.
/// </summary>
public static class AguiArgumentNormalizer
{
    public static object? Normalize(object? value) =>
        value switch
        {
            null => null,
            string s => s,
            bool b => b,
            JsonElement el => el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                // Keep numeric text — do not box Double/Int64.
                JsonValueKind.Number => el.GetRawText(),
                _ => el.GetRawText()
            },
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString()
        };

    public static IDictionary<string, object?>? NormalizeDictionary(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return args;
        }

        var safe = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in args)
        {
            safe[key] = Normalize(value);
        }

        return safe;
    }
}
