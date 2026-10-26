using System.Text.RegularExpressions;

namespace Orbit.Contracts;

/// <summary>
/// Pure MCP config validation/normalization (no IO). Used by Runtime PUT and unit tests.
/// </summary>
public static partial class McpConfigRules
{
    public static (McpConfigFile Normalized, IReadOnlyList<string> Warnings) ValidateAndNormalize(McpConfigFile? file)
    {
        if (file is null)
        {
            throw new ArgumentException(
                "mcp_body_required: PUT body must be a JSON object with a 'servers' array.");
        }

        if (file.Servers is null)
        {
            throw new ArgumentException(
                "mcp_servers_required: 'servers' array is required (use [] to clear all MCP servers).");
        }

        var warnings = new List<string>();
        var servers = new List<McpServerConfig>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in file.Servers)
        {
            if (raw is null)
            {
                throw new ArgumentException("MCP servers list must not contain null entries.");
            }

            if (string.IsNullOrWhiteSpace(raw.Id))
            {
                throw new ArgumentException("Each MCP server requires a non-empty id.");
            }

            var id = raw.Id.Trim();
            if (!ServerIdRegex().IsMatch(id))
            {
                throw new ArgumentException(
                    $"Invalid MCP server id '{id}'. Use lowercase letters, digits, and hyphens only (^[a-z0-9-]+$).");
            }

            if (!seen.Add(id))
            {
                throw new ArgumentException($"Duplicate MCP server id '{id}'.");
            }

            var transport = (raw.Transport ?? "stdio").Trim().ToLowerInvariant();
            if (transport is not ("stdio" or "http"))
            {
                throw new ArgumentException($"MCP server '{id}': transport must be stdio or http.");
            }

            string? command = null;
            string? url = null;
            if (transport == "stdio")
            {
                if (string.IsNullOrWhiteSpace(raw.Command))
                {
                    throw new ArgumentException($"MCP server '{id}': stdio transport requires command.");
                }

                command = raw.Command.Trim();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(raw.Url)
                    || !Uri.TryCreate(raw.Url.Trim(), UriKind.Absolute, out var uri)
                    || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"MCP server '{id}': http transport requires an https url.");
                }

                url = uri.ToString();
            }

            var env = NormalizeMap(raw.Env);
            var headers = NormalizeMap(raw.Headers);
            foreach (var (key, value) in headers)
            {
                if (LooksLikePlaintextSecret(value))
                {
                    warnings.Add($"Server '{id}' header '{key}' looks like a plaintext secret; prefer ${{VAR}}.");
                }
            }

            foreach (var (key, value) in env)
            {
                if (LooksLikePlaintextSecret(value))
                {
                    warnings.Add($"Server '{id}' env '{key}' looks like a plaintext secret; prefer ${{VAR}}.");
                }
            }

            var toolPrefix = NormalizeToolPrefix(raw.ToolPrefix, id);

            servers.Add(new McpServerConfig
            {
                Id = id,
                Transport = transport,
                Command = command,
                Args = (raw.Args ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
                Env = env,
                Url = url,
                Headers = headers,
                Enabled = raw.Enabled,
                Trusted = raw.Trusted,
                ToolPrefix = toolPrefix,
                TimeoutSeconds = raw.TimeoutSeconds <= 0 ? 30 : Math.Clamp(raw.TimeoutSeconds, 5, 300),
                RequiresApproval = raw.RequiresApproval,
                ApprovalTools = (raw.ApprovalTools ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        if (servers.Count == 0)
        {
            warnings.Add(
                "MCP servers list is empty — all MCP servers for this agent will be removed after restart.");
        }

        return (new McpConfigFile { Revision = file.Revision, Servers = servers }, warnings);
    }

    /// <summary>Length &gt; 40 and no <c>${VAR}</c> placeholder → likely pasted secret.</summary>
    public static bool LooksLikePlaintextSecret(string value) =>
        value.Length > 40 && !value.Contains("${", StringComparison.Ordinal);

    public static bool IsValidServerId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ServerIdRegex().IsMatch(id.Trim());

    /// <summary>
    /// null → leave null (loader uses mcp_{id}_); "" → keep empty (no prefix);
    /// otherwise 1–32 chars of [A-Za-z0-9_-].
    /// </summary>
    public static string? NormalizeToolPrefix(string? toolPrefix, string serverId)
    {
        if (toolPrefix is null)
        {
            return null;
        }

        if (toolPrefix.Length == 0)
        {
            return "";
        }

        var trimmed = toolPrefix.Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        if (trimmed.Length > 32 || !ToolPrefixRegex().IsMatch(trimmed))
        {
            throw new ArgumentException(
                $"MCP server '{serverId}': toolPrefix must be empty or 1–32 chars of [A-Za-z0-9_-].");
        }

        return trimmed;
    }

    private static Dictionary<string, string> NormalizeMap(Dictionary<string, string>? input)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input is null)
        {
            return map;
        }

        foreach (var (key, value) in input)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                continue;
            }

            map[key.Trim()] = value;
        }

        return map;
    }

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex ServerIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ToolPrefixRegex();
}
