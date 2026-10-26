using System.Text.RegularExpressions;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting.Mcp;

/// <summary>
/// ${VAR} expansion + tool alias uniquify (pure helpers used by <see cref="McpToolHost"/>).
/// </summary>
public static partial class McpVariableExpansion
{
    public static bool TryExpand(string? template, out string? resolved, out string? missingVar)
    {
        resolved = template;
        missingVar = null;
        if (string.IsNullOrEmpty(template) || !template.Contains("${", StringComparison.Ordinal))
        {
            return true;
        }

        var result = template;
        foreach (Match match in EnvPlaceholderRegex().Matches(template))
        {
            var name = match.Groups[1].Value;
            var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
                        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            if (value is null)
            {
                missingVar = name;
                resolved = null;
                return false;
            }

            result = result.Replace(match.Value, value, StringComparison.Ordinal);
        }

        resolved = result;
        return true;
    }

    public static bool TryExpandEnvMap(
        Dictionary<string, string>? input,
        out Dictionary<string, string?> expanded,
        out string? error)
    {
        expanded = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (input is null || input.Count == 0)
        {
            return true;
        }

        foreach (var (key, value) in input)
        {
            if (!TryExpand(value, out var resolved, out var missing))
            {
                error = $"missing env {missing}";
                return false;
            }

            expanded[key] = resolved;
        }

        return true;
    }

    public static bool TryExpandHeaderMap(
        Dictionary<string, string>? input,
        out Dictionary<string, string> expanded,
        out string? error)
    {
        expanded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        if (input is null || input.Count == 0)
        {
            return true;
        }

        foreach (var (key, value) in input)
        {
            if (!TryExpand(value, out var resolved, out var missing) || resolved is null)
            {
                error = $"missing env {missing}";
                return false;
            }

            expanded[key] = resolved;
        }

        return true;
    }

    public static string UniqueName(string desired, HashSet<string> used)
    {
        if (!used.Contains(desired))
        {
            return desired;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{desired}_{i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{desired}_{Guid.NewGuid():N}";
    }

    public static string SanitizeToolPrefix(string? toolPrefix, string serverId)
    {
        // Spec: ToolPrefix ?? mcp_{id}_ ; empty string keeps server tool names as-is.
        return toolPrefix ?? $"mcp_{SanitizeId(serverId)}_";
    }

    public static string SanitizeId(string id) => OrbitPathNames.SanitizeFileName(id);

    public static string RedactBearer(string message) =>
        BearerRegex().Replace(message, "Bearer ***");

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex EnvPlaceholderRegex();

    [GeneratedRegex(@"Bearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();
}
