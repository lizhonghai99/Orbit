using System.Text.Json;

namespace Orbit.Contracts;

/// <summary>
/// Safe payload field readers shared by Runtime and Shell.
/// <see cref="JsonElement.GetString"/> throws on non-string kinds — these return null instead.
/// </summary>
public static class SessionPayloadJson
{
    public static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    public static string? ReadString(SessionEntry entry, string name) =>
        entry.Payload is JsonElement payload ? ReadString(payload, name) : null;
}
