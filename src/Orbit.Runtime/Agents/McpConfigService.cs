using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Contracts;
using Orbit.Runtime.Agui;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;

namespace Orbit.Runtime.Agents;

/// <summary>
/// Persists per-agent MCP server lists under <c>data/mcp/{agentId}.json</c>.
/// Connection happens only on Agent process start (no hot-plug).
/// </summary>
public sealed class McpConfigService(
    RepoPaths paths,
    AgentRegistry registry,
    ISessionStore sessions,
    IToolSchemaCache toolSchemaCache,
    ILogger<McpConfigService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();

    public string McpDir => Path.Combine(paths.DataDir, "mcp");
    public string AuditDir => Path.Combine(paths.DataDir, "audit");

    public McpConfigFile Load(string agentId)
    {
        EnsureKnown(agentId);
        lock (_gate)
        {
            return LoadUnlocked(agentId);
        }
    }

    public McpSaveResult Put(string agentId, McpConfigFile body)
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureKnown(agentId);
        lock (_gate)
        {
            var onDisk = LoadUnlocked(agentId);
            if (body.Revision != onDisk.Revision)
            {
                throw new OrbitApiException(
                    OrbitErrorCodes.Conflict,
                    "mcp_config_stale: config changed elsewhere — reload and save again.",
                    new Dictionary<string, string>
                    {
                        ["expectedRevision"] = onDisk.Revision.ToString(),
                        ["presentedRevision"] = body.Revision.ToString()
                    });
            }

            var (normalized, warnings) = McpConfigRules.ValidateAndNormalize(body);
            var toWrite = new McpConfigFile
            {
                Revision = onDisk.Revision + 1,
                Servers = normalized.Servers!.ToList()
            };
            AtomicWrite(agentId, toWrite);
            toolSchemaCache.Invalidate(agentId);
            Audit(agentId, action: "put", serverId: null);
            return new McpSaveResult
            {
                Saved = true,
                RequiresRestart = true,
                Servers = toWrite.Servers!.Count,
                Warnings = warnings
            };
        }
    }

    public McpMutationResult SetTrust(string agentId, string serverId, bool trusted)
    {
        EnsureKnown(agentId);
        lock (_gate)
        {
            var file = LoadUnlocked(agentId);
            var servers = file.Servers!;
            var idx = servers.FindIndex(s =>
                string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                throw new KeyNotFoundException($"MCP server '{serverId}' not found.");
            }

            var old = servers[idx];
            servers[idx] = Clone(old, trusted: trusted);
            var (normalized, _) = McpConfigRules.ValidateAndNormalize(file);
            AtomicWrite(agentId, new McpConfigFile
            {
                Revision = file.Revision + 1,
                Servers = normalized.Servers!.ToList()
            });
            toolSchemaCache.Invalidate(agentId);
            Audit(agentId, action: trusted ? "trust" : "untrust", serverId: old.Id);
            return new McpMutationResult { RequiresRestart = true, ServerId = old.Id };
        }
    }

    public McpMutationResult Delete(string agentId, string serverId)
    {
        EnsureKnown(agentId);
        lock (_gate)
        {
            var file = LoadUnlocked(agentId);
            var removed = file.Servers!.RemoveAll(s =>
                string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                throw new KeyNotFoundException($"MCP server '{serverId}' not found.");
            }

            var (normalized, _) = McpConfigRules.ValidateAndNormalize(file);
            AtomicWrite(agentId, new McpConfigFile
            {
                Revision = file.Revision + 1,
                Servers = normalized.Servers!.ToList()
            });
            toolSchemaCache.Invalidate(agentId);
            Audit(agentId, action: "delete", serverId: serverId);
            return new McpMutationResult { RequiresRestart = true, ServerId = serverId };
        }
    }

    private McpConfigFile LoadUnlocked(string agentId)
    {
        var path = PathFor(agentId);
        if (!File.Exists(path))
        {
            return new McpConfigFile { Servers = [] };
        }

        try
        {
            var dto = JsonSerializer.Deserialize<McpConfigFile>(File.ReadAllText(path), JsonOptions);
            if (dto is null)
            {
                return new McpConfigFile { Servers = [] };
            }

            // Legacy / corrupt omit: treat as empty list for in-memory use (never null after Load).
            dto.Servers ??= [];
            return dto;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Bad MCP config JSON for {AgentId}", agentId);
            throw new InvalidDataException(
                $"MCP config for '{agentId}' is corrupt JSON. Fix or delete data/mcp/{agentId}.json.", ex);
        }
    }

    private void AtomicWrite(string agentId, McpConfigFile file)
    {
        Directory.CreateDirectory(McpDir);
        var path = PathFor(agentId);
        var json = JsonSerializer.Serialize(file, JsonOptions);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Append one audit line to <c>data/audit/mcp.jsonl</c> (not every active session).
    /// Also stamps the newest active session for that agent when one exists (cheap UX breadcrumb).
    /// </summary>
    private void Audit(string agentId, string action, string? serverId)
    {
        try
        {
            Directory.CreateDirectory(AuditDir);
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow,
                agentId,
                action,
                serverId,
                source = "shell"
            }, AuditJsonOptions);
            File.AppendAllText(Path.Combine(AuditDir, "mcp.jsonl"), line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append MCP audit line for {AgentId}", agentId);
        }

        // Single breadcrumb on the most recently updated active session (avoid N× JSONL rewrite).
        var latest = sessions.List(includeArchived: false)
            .Where(s => string.Equals(s.AgentId, agentId, StringComparison.OrdinalIgnoreCase) && !s.IsArchived)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault();
        if (latest is null)
        {
            return;
        }

        try
        {
            sessions.Append(latest.SessionId, SessionEvents.Meta(
                SessionMetaKinds.McpChanged,
                new
                {
                    action,
                    serverId,
                    source = "shell"
                }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append mcp_changed for session {SessionId}", latest.SessionId);
        }
    }

    private void EnsureKnown(string agentId)
    {
        if (!registry.TryGet(agentId, out _))
        {
            throw new KeyNotFoundException($"Unknown agent '{agentId}'.");
        }
    }

    private string PathFor(string agentId) =>
        Path.Combine(McpDir, $"{OrbitPathNames.SanitizeFileName(agentId)}.json");

    private static McpServerConfig Clone(McpServerConfig src, bool? trusted = null) => new()
    {
        Id = src.Id,
        Transport = src.Transport,
        Command = src.Command,
        Args = src.Args.ToList(),
        Env = new Dictionary<string, string>(src.Env, StringComparer.OrdinalIgnoreCase),
        Url = src.Url,
        Headers = new Dictionary<string, string>(src.Headers, StringComparer.OrdinalIgnoreCase),
        Enabled = src.Enabled,
        Trusted = trusted ?? src.Trusted,
        ToolPrefix = src.ToolPrefix,
        TimeoutSeconds = src.TimeoutSeconds,
        RequiresApproval = src.RequiresApproval,
        ApprovalTools = src.ApprovalTools.ToList()
    };
}
