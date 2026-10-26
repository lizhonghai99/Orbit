using System.Text.Json;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Describes the Harness modules wired by <see cref="AgentBootstrap"/> — source of truth for Agent <c>/health</c>
/// and Shell capability panel (not a second invented inventory).
/// </summary>
public static class HarnessProfile
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>When manifest/env omit harness: core default (disk / Background still opt-in).</summary>
    public static IReadOnlyList<string> DefaultSelectableIds => OrbitHarnessIds.Default;

    public const string StyleBase = "hand-assembled Standard";

    /// <summary>
    /// Resolve enabled selectable module ids.
    /// Priority: <paramref name="overrideList"/> → <c>ORBIT_HARNESS</c> →
    /// <c>agents/{id}.agent.json</c> harness → default.
    /// </summary>
    public static IReadOnlySet<string> ResolveEnabled(
        IEnumerable<string>? overrideList = null,
        string? agentId = null) =>
        ResolveParse(overrideList, agentId).Enabled;

    public static OrbitHarnessIds.ParseResult ResolveParse(
        IEnumerable<string>? overrideList = null,
        string? agentId = null)
    {
        IEnumerable<string>? source = overrideList;
        if (source is null)
        {
            var env = Environment.GetEnvironmentVariable("ORBIT_HARNESS");
            if (!string.IsNullOrWhiteSpace(env))
            {
                source = env.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else
            {
                source = TryReadManifestHarness(agentId);
            }
        }

        return OrbitHarnessIds.Parse(source);
    }

    /// <summary>
    /// Read <c>harness</c> from <c>{ORBIT_ROOT}/agents/{agentId}.agent.json</c> when present.
    /// </summary>
    public static IReadOnlyList<string>? TryReadManifestHarness(string? agentId, string? contentRoot = null)
    {
        agentId ??= Environment.GetEnvironmentVariable("ORBIT_AGENT_ID");
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        // Explicit contentRoot is a call-site override — do not let ORBIT_ROOT steal it
        // (ResolveDataRoot prefers env, which races under parallel tests and surprises callers).
        var root = !string.IsNullOrWhiteSpace(contentRoot)
            ? contentRoot
            : OrbitPathNames.ResolveDataRoot();
        var path = Path.Combine(root, "agents", $"{OrbitPathNames.SanitizeFileName(agentId.Trim())}.agent.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<AgentManifest>(File.ReadAllText(path), ManifestJson);
            return manifest?.Harness is { Count: > 0 } h ? h : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static AgentHarnessDto Describe(string agentId, string? contentRoot = null, IReadOnlySet<string>? enabled = null)
    {
        OrbitHarnessIds.ParseResult? parse = null;
        if (enabled is null)
        {
            // Prefer the set AgentBootstrap actually wired (auto tool_approval etc.).
            enabled = AgentCapabilityHints.GetEffectiveHarness(agentId);
            if (enabled is null)
            {
                parse = ResolveParse(agentId: agentId);
                enabled = parse.Enabled;
            }
        }

        var ignored = parse?.Ignored ?? [];
        var modelId = ChatClientFactory.DescribeModel();
        var window = OrbitCompaction.ResolveContextWindowTokens(modelId);
        var maxOut = OrbitCompaction.ResolveMaxOutputTokens();
        var memoryRoot = ResolveFileMemoryRoot(agentId, contentRoot);
        var accessRoot = ResolveFileAccessRoot(contentRoot);
        var skillsDir = SkillCatalog.ResolveSkillsDirectory(agentId, contentRoot);
        var bg = AgentCapabilityHints.GetBackgroundAgents(agentId);
        var hasBgChildren = bg is { Count: > 0 };
        var wantBg = enabled.Contains(OrbitHarnessIds.Background) && hasBgChildren;
        var accessRootExists = Directory.Exists(accessRoot);

        var style = StyleBase;
        if (enabled.Contains(OrbitHarnessIds.FileAccess))
        {
            style += "+FileAccess";
        }

        if (enabled.Contains(OrbitHarnessIds.FileMemory))
        {
            style += "+FileMemory";
        }

        if (wantBg)
        {
            style += "+BackgroundAgents";
        }

        var modules = new List<HarnessModuleDto>();
        if (enabled.Contains(OrbitHarnessIds.Mode))
        {
            modules.Add(new HarnessModuleDto
            {
                Id = OrbitHarnessIds.Mode,
                Title = "AgentModeProvider",
                Kind = "provider",
                Enabled = true,
                Detail = "mode_get / mode_set · plan→execute"
            });
        }

        if (enabled.Contains(OrbitHarnessIds.Todo))
        {
            modules.Add(new HarnessModuleDto
            {
                Id = OrbitHarnessIds.Todo,
                Title = "TodoProvider",
                Kind = "provider",
                Enabled = true,
                Detail = "todos_* · session checklist"
            });
        }

        if (enabled.Contains(OrbitHarnessIds.FileMemory))
        {
            modules.Add(new HarnessModuleDto
            {
                Id = OrbitHarnessIds.FileMemory,
                Title = "FileMemoryProvider",
                Kind = "provider",
                Enabled = true,
                Detail = $"file_memory_* · MEMORY.md · {ShortPath(memoryRoot)}"
            });
        }

        if (enabled.Contains(OrbitHarnessIds.FileAccess))
        {
            modules.Add(new HarnessModuleDto
            {
                Id = OrbitHarnessIds.FileAccess,
                Title = "FileAccessProvider",
                Kind = "provider",
                Enabled = accessRootExists,
                Detail = accessRootExists
                    ? $"file_access_* · {ShortPath(accessRoot)} · read auto / write HITL"
                    : $"file_access_* · root missing: {ShortPath(accessRoot)}"
            });
        }

        if (enabled.Contains(OrbitHarnessIds.Background))
        {
            modules.Add(new HarnessModuleDto
            {
                // Display id follows framework naming; enabled token stays OrbitHarnessIds.Background.
                Id = "background_agents",
                Title = "BackgroundAgentsProvider",
                Kind = "provider",
                Enabled = wantBg,
                Detail = wantBg
                    ? $"background_agents_* · children: {string.Join(", ", bg!)}"
                    : "listed in harness but no children registered"
            });
        }

        if (enabled.Contains(OrbitHarnessIds.Skills))
        {
            modules.Add(new HarnessModuleDto
            {
                Id = OrbitHarnessIds.Skills,
                Title = "AgentSkillsProvider",
                Kind = "provider",
                Enabled = skillsDir is not null,
                Detail = skillsDir is null
                    ? "no skills/ directory"
                    : $"load_skill · {ShortPath(skillsDir)} · read auto"
            });
        }

        if (enabled.Contains(OrbitHarnessIds.Compaction))
        {
            modules.Add(new HarnessModuleDto
            {
                Id = OrbitHarnessIds.Compaction,
                Title = "ContextWindowCompaction",
                Kind = "pipeline",
                Enabled = true,
                Detail = $"{FormatK(window)} window / {FormatK(maxOut)} out · CompactionProvider + AsChatReducer"
            });
        }

        // ask_user_* is always ApprovalRequired — show tool_approval even when the manifest omitted it
        // (AgentBootstrap auto-enables UseToolApproval in that case).
        var toolApprovalListed = enabled.Contains(OrbitHarnessIds.ToolApproval);
        modules.Add(new HarnessModuleDto
        {
            Id = OrbitHarnessIds.ToolApproval,
            Title = "UseToolApproval",
            Kind = "middleware",
            Enabled = true,
            Detail = toolApprovalListed
                ? (enabled.Contains(OrbitHarnessIds.FileAccess)
                    ? "ApprovalRequired + Skills/FileAccess ReadOnly auto-rules"
                    : "ApprovalRequired + Skills ReadOnly auto-rule")
                : "auto-enabled (ask_user_* / ApprovalRequired tools); add tool_approval to harness"
        });

        return new AgentHarnessDto
        {
            Style = style,
            Modules = modules,
            IgnoredTokens = ignored
        };
    }

    public static string ResolveFileMemoryRoot(string agentId, string? contentRoot)
    {
        // Prefer repo root so FileMemory + mailboxes.md survive bin/ clean rebuilds.
        var root = OrbitPathNames.ResolveDataRoot(contentRoot);
        return Path.GetFullPath(Path.Combine(root, "data", "file-memory", OrbitPathNames.SanitizeFileName(agentId)));
    }

    public static string ResolveFileAccessRoot(string? contentRoot)
    {
        // Prefer ambient / ORBIT_WORKSPACE when present (process default for health/describe).
        var ambient = Environment.GetEnvironmentVariable("ORBIT_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(ambient))
        {
            return Path.GetFullPath(ambient.Trim());
        }

        var env = Environment.GetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT")
                  ?? Environment.GetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT", EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env.Trim());
        }

        var root = string.IsNullOrWhiteSpace(contentRoot) ? AppContext.BaseDirectory : contentRoot;
        return Path.GetFullPath(Path.Combine(root, "data", "file-access"));
    }

    private static string ShortPath(string path)
    {
        if (path.Length <= 42)
        {
            return path;
        }

        return "…" + path[^39..];
    }

    private static string FormatK(int tokens) =>
        tokens >= 1000 ? $"{tokens / 1000.0:0.#}K" : tokens.ToString();
}
