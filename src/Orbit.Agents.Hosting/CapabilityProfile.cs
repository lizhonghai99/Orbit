using System.Collections.Concurrent;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Process-local enforcement profile. Catalog/schema stay visible; disabled tools/skills are rejected at invoke time.
/// Hiding tools from schema would require rebuilding the Agent — not done this round.
/// </summary>
public sealed class CapabilityProfile
{
    public static CapabilityProfile Instance { get; } = new();

    private readonly object _gate = new();
    private HashSet<string> _disabledSkills = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _disabledTools = new(StringComparer.OrdinalIgnoreCase);

    public void Apply(CapabilityPatchRequest request)
    {
        lock (_gate)
        {
            if (request.DisabledSkills is not null)
            {
                _disabledSkills = ToSet(request.DisabledSkills);
            }

            if (request.DisabledTools is not null)
            {
                _disabledTools = ToSet(request.DisabledTools);
            }
        }
    }

    public CapabilityProfileDto Snapshot()
    {
        lock (_gate)
        {
            return new CapabilityProfileDto
            {
                DisabledSkills = _disabledSkills.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
                DisabledTools = _disabledTools.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }
    }

    public bool IsToolDisabled(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        lock (_gate)
        {
            return _disabledTools.Contains(toolName.Trim());
        }
    }

    public bool IsSkillDisabled(string? skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            return false;
        }

        lock (_gate)
        {
            return _disabledSkills.Contains(skillId.Trim());
        }
    }

    private static HashSet<string> ToSet(IEnumerable<string> values) =>
        values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
