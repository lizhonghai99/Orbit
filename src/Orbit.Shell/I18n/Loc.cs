using System.Globalization;
using System.Text.Json;

namespace Orbit.Shell.I18n;

public enum UiLocale
{
    En,
    Zh
}

/// <summary>
/// Tiny UI localization. Keys are English identifiers; Chinese lives only in <c>strings.zh.json</c>.
/// Preference: <c>ORBIT_UI_LOCALE=en|zh</c> (user env).
/// </summary>
public static class Loc
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private static UiLocale _locale = UiLocale.En;

    public static UiLocale Current => _locale;

    public static event Action? Changed;

    public static void Initialize()
    {
        Set(ReadSavedLocale(), persist: false);
    }

    public static void Set(UiLocale locale, bool persist = true)
    {
        if (_locale == locale && _map.Count > 0)
        {
            if (persist)
            {
                SaveLocale(locale);
            }

            return;
        }

        _locale = locale;
        _map = Load(locale);
        if (persist)
        {
            SaveLocale(locale);
        }

        Changed?.Invoke();
    }

    public static string T(string key, params object[] args)
    {
        if (!_map.TryGetValue(key, out var template) || string.IsNullOrEmpty(template))
        {
            template = key;
        }

        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    public static string AgentName(string agentId, string? fallback = null)
    {
        var key = $"agent.{agentId.ToLowerInvariant()}.name";
        return _map.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : fallback ?? agentId;
    }

    public static string AgentDescription(string agentId, string? fallback = null)
    {
        var key = $"agent.{agentId.ToLowerInvariant()}.description";
        return _map.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : fallback ?? "";
    }

    public static UiLocale Parse(string? value) =>
        string.Equals(value, "zh", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "zh-cn", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "chinese", StringComparison.OrdinalIgnoreCase)
            ? UiLocale.Zh
            : UiLocale.En;

    public static string ToStorage(UiLocale locale) => locale == UiLocale.Zh ? "zh" : "en";

    private static UiLocale ReadSavedLocale()
    {
        var env = Environment.GetEnvironmentVariable("ORBIT_UI_LOCALE", EnvironmentVariableTarget.User)
                  ?? Environment.GetEnvironmentVariable("ORBIT_UI_LOCALE");
        return Parse(env);
    }

    private static void SaveLocale(UiLocale locale)
    {
        var value = ToStorage(locale);
        // Process is instant; User writes the Windows registry and can stall the UI thread.
        Environment.SetEnvironmentVariable("ORBIT_UI_LOCALE", value, EnvironmentVariableTarget.Process);
        var existing = Environment.GetEnvironmentVariable("ORBIT_UI_LOCALE", EnvironmentVariableTarget.User);
        if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("ORBIT_UI_LOCALE", value, EnvironmentVariableTarget.User);
            }
            catch
            {
                // Preference write is best-effort.
            }
        });
    }

    private static Dictionary<string, string> Load(UiLocale locale)
    {
        var file = locale == UiLocale.Zh ? "strings.zh.json" : "strings.en.json";
        var path = Path.Combine(AppContext.BaseDirectory, "I18n", file);
        if (!File.Exists(path))
        {
            // Design-time / incomplete publish: fall back to embedded sibling next to source if present.
            path = Path.Combine(AppContext.BaseDirectory, file);
        }

        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
