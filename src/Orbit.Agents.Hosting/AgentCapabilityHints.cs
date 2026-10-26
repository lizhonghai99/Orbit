using System.Collections.Concurrent;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Soft capability hints for <c>/health</c> / Shell (BackgroundAgents names, effective harness, etc.).
/// </summary>
public static class AgentCapabilityHints
{
    private static readonly ConcurrentDictionary<string, string[]> BackgroundAgents =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> EffectiveHarness =
        new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterBackgroundAgents(string agentId, IEnumerable<string> names)
    {
        var list = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (list.Length == 0)
        {
            BackgroundAgents.TryRemove(agentId, out _);
            return;
        }

        BackgroundAgents[agentId] = list;
    }

    public static IReadOnlyList<string>? GetBackgroundAgents(string agentId) =>
        BackgroundAgents.TryGetValue(agentId, out var names) ? names : null;

    /// <summary>
    /// Persist the harness set actually wired by <see cref="AgentBootstrap"/>
    /// (includes auto-added <c>tool_approval</c>) so <c>/health</c> does not re-parse env alone.
    /// </summary>
    public static void RegisterEffectiveHarness(string agentId, IReadOnlySet<string> enabled)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        EffectiveHarness[agentId.Trim()] = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlySet<string>? GetEffectiveHarness(string agentId) =>
        EffectiveHarness.TryGetValue(agentId, out var set) ? set : null;
}
