using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Contracts;
using Orbit.Runtime.Agui;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;

namespace Orbit.Runtime.Agents;

/// <summary>
/// Aggregates Agent capability surfaces and persists disable-list profiles for Shell.
/// </summary>
public sealed class CapabilityService(
    RepoPaths paths,
    AgentRegistry registry,
    CapabilityProfileStore profileStore,
    ISessionStore sessions,
    IHttpClientFactory httpFactory,
    IToolSchemaCache toolSchemaCache,
    ILogger<CapabilityService> logger)
{
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _patchGate = new();

    public string AuditDir => Path.Combine(paths.DataDir, "audit");

    public async Task<AgentCapabilitiesDto> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var profile = profileStore.Load(agentId);
        var disabledSkills = profile.DisabledSkills.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabledTools = profile.DisabledTools.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!registry.TryGet(agentId, out var agent)
            || agent.Lifecycle != AgentLifecycle.Running
            || string.IsNullOrWhiteSpace(agent.BaseUrl))
        {
            return Offline(agentId);
        }

        var baseUrl = agent.BaseUrl.TrimEnd('/');
        using var http = httpFactory.CreateClient("agent-health");

        var healthTask = SafeGetAsync<AgentProcessHealthDto>(http, $"{baseUrl}/health", cancellationToken);
        var skillsTask = SafeGetAsync<List<SkillSummaryDto>>(http, $"{baseUrl}/skills", cancellationToken);
        var toolsTask = SafeGetAsync<List<ToolSchemaDto>>(http, $"{baseUrl}/tools", cancellationToken);
        var mcpTask = SafeGetAsync<McpStatusResponse>(http, $"{baseUrl}/mcp", cancellationToken);
        await Task.WhenAll(healthTask, skillsTask, toolsTask, mcpTask).ConfigureAwait(false);

        var health = await healthTask.ConfigureAwait(false);
        var skills = await skillsTask.ConfigureAwait(false) ?? [];
        var tools = await toolsTask.ConfigureAwait(false) ?? [];
        var mcp = await mcpTask.ConfigureAwait(false);

        return new AgentCapabilitiesDto
        {
            AgentId = agentId,
            Offline = false,
            Model = health?.Model,
            Reasoning = string.IsNullOrWhiteSpace(health?.Reasoning) ? "default" : health.Reasoning,
            Harness = health?.Harness,
            Skills = skills.Select(s => ToSkill(s, enabled: !disabledSkills.Contains(s.Id))).ToList(),
            // MCP tools stay in the MCP panel only — avoid dual toggles fighting over the same name.
            Tools = tools
                .Where(t => !string.Equals(t.Source, "mcp", StringComparison.OrdinalIgnoreCase))
                .Select(t => ToTool(t, enabled: !disabledTools.Contains(t.Name)))
                .ToList(),
            Mcp = (mcp?.Servers ?? []).Select(ToMcp).ToList()
        };
    }

    public async Task<CapabilityPatchResult> PatchAsync(
        string agentId,
        CapabilityPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!registry.TryGet(agentId, out _))
        {
            throw new KeyNotFoundException($"Unknown agent '{agentId}'.");
        }

        // Serialize read-modify-write so concurrent Shell patches cannot clobber each other.
        CapabilityProfileDto before;
        CapabilityProfileDto after;
        lock (_patchGate)
        {
            before = profileStore.Load(agentId);
            after = new CapabilityProfileDto
            {
                DisabledSkills = request.DisabledSkills ?? before.DisabledSkills,
                DisabledTools = request.DisabledTools ?? before.DisabledTools
            };
            profileStore.Save(agentId, after);
        }

        if (registry.TryGet(agentId, out var agent)
            && agent.Lifecycle == AgentLifecycle.Running
            && !string.IsNullOrWhiteSpace(agent.BaseUrl))
        {
            try
            {
                using var http = httpFactory.CreateClient("agent-health");
                using var response = await http.PutAsJsonAsync(
                    $"{agent.BaseUrl.TrimEnd('/')}/capabilities",
                    new CapabilityPatchRequest
                    {
                        DisabledSkills = after.DisabledSkills,
                        DisabledTools = after.DisabledTools
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lock (_patchGate)
                    {
                        profileStore.Save(agentId, before);
                    }

                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"Agent capabilities apply failed ({(int)response.StatusCode}); profile rolled back. {body}");
                }
            }
            catch (HttpRequestException)
            {
                lock (_patchGate)
                {
                    profileStore.Save(agentId, before);
                }

                throw;
            }
            catch (Exception ex)
            {
                lock (_patchGate)
                {
                    profileStore.Save(agentId, before);
                }

                throw new HttpRequestException(
                    $"Agent capabilities apply failed; profile rolled back. {ex.Message}", ex);
            }
        }

        toolSchemaCache.Invalidate(agentId);

        var affected = AuditCapabilityChange(agentId, before, after);

        return new CapabilityPatchResult
        {
            Applied = ["skills", "tools"],
            // MCP / model hot-swap not implemented this round.
            RequiresRestart = [],
            AffectedSessions = affected
        };
    }

    /// <summary>
    /// Append one audit line to <c>data/audit/capabilities.jsonl</c> (not every active session).
    /// Also stamps the newest active session for that agent when one exists (same shape as MCP audit).
    /// </summary>
    private int AuditCapabilityChange(string agentId, CapabilityProfileDto before, CapabilityProfileDto after)
    {
        try
        {
            Directory.CreateDirectory(AuditDir);
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow,
                agentId,
                action = "patch",
                before,
                after,
                source = "shell"
            }, AuditJsonOptions);
            File.AppendAllText(Path.Combine(AuditDir, "capabilities.jsonl"), line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append capability audit line for {AgentId}", agentId);
        }

        var latest = sessions.List(includeArchived: false)
            .Where(s => string.Equals(s.AgentId, agentId, StringComparison.OrdinalIgnoreCase) && !s.IsArchived)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault();
        if (latest is null)
        {
            return 0;
        }

        try
        {
            sessions.Append(latest.SessionId, SessionEvents.Meta(
                SessionMetaKinds.CapabilityChanged,
                new
                {
                    before,
                    after,
                    source = "shell"
                }));
            return 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append capability_changed for session {SessionId}", latest.SessionId);
            return 0;
        }
    }

    public async Task PushProfileAsync(string agentId, string baseUrl, CancellationToken cancellationToken = default)
    {
        var profile = profileStore.Load(agentId);
        if (profile.DisabledSkills.Count == 0 && profile.DisabledTools.Count == 0)
        {
            return;
        }

        using var http = httpFactory.CreateClient("agent-health");
        using var response = await http.PutAsJsonAsync(
            $"{baseUrl.TrimEnd('/')}/capabilities",
            new CapabilityPatchRequest
            {
                DisabledSkills = profile.DisabledSkills,
                DisabledTools = profile.DisabledTools
            },
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T?> SafeGetAsync<T>(HttpClient http, string url, CancellationToken ct)
        where T : class
    {
        try
        {
            return await http.GetFromJsonAsync<T>(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Capabilities fetch failed for {Url}", url);
            return null;
        }
    }

    private static AgentCapabilitiesDto Offline(string agentId) => new()
    {
        AgentId = agentId,
        Offline = true,
        Model = null,
        Reasoning = null,
        Harness = null,
        Skills = [],
        Tools = [],
        Mcp = []
    };

    private static CapabilitySkillDto ToSkill(SkillSummaryDto s, bool enabled) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Description = s.Description,
        Source = string.IsNullOrWhiteSpace(s.Source) ? "builtin" : s.Source,
        Enabled = enabled,
        HasScripts = s.HasScripts,
        Sha256 = s.Sha256,
        Path = s.Path
    };

    private static CapabilityToolDto ToTool(ToolSchemaDto t, bool enabled) => new()
    {
        Name = t.Name,
        Description = t.Description,
        RequiresApproval = t.RequiresApproval,
        Source = string.IsNullOrWhiteSpace(t.Source) ? "domain" : t.Source,
        Enabled = enabled
    };

    private static CapabilityMcpServerDto ToMcp(McpServerStatusDto s) => new()
    {
        Id = s.Id,
        Transport = s.Transport,
        Status = s.Status,
        ToolCount = s.ToolCount,
        Trusted = s.Trusted,
        Enabled = s.Enabled,
        Error = s.Error,
        ToolNames = s.Tools.Select(t => t.Name).ToArray()
    };
}
