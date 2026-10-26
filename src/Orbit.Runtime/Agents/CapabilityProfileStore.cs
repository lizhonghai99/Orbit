using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;

namespace Orbit.Runtime.Agents;

/// <summary>Persists per-agent capability disable lists under <c>data/profiles/{agentId}.json</c>.</summary>
public sealed class CapabilityProfileStore(RepoPaths paths, ILogger<CapabilityProfileStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string ProfilesDir => Path.Combine(paths.DataDir, "profiles");

    public CapabilityProfileDto Load(string agentId)
    {
        var file = PathFor(agentId);
        if (!File.Exists(file))
        {
            return Empty();
        }

        try
        {
            var json = File.ReadAllText(file);
            var dto = JsonSerializer.Deserialize<CapabilityProfileDto>(json, JsonOptions);
            return Normalize(dto);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bad capability profile JSON for {AgentId}; treating as empty", agentId);
            return Empty();
        }
    }

    public void Save(string agentId, CapabilityProfileDto profile)
    {
        Directory.CreateDirectory(ProfilesDir);
        var file = PathFor(agentId);
        var normalized = Normalize(profile);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var temp = file + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }

    private string PathFor(string agentId) =>
        Path.Combine(ProfilesDir, $"{OrbitPathNames.SanitizeFileName(agentId)}.json");

    private static CapabilityProfileDto Empty() => new()
    {
        DisabledSkills = [],
        DisabledTools = []
    };

    private static CapabilityProfileDto Normalize(CapabilityProfileDto? dto) => new()
    {
        DisabledSkills = Distinct(dto?.DisabledSkills),
        DisabledTools = Distinct(dto?.DisabledTools)
    };

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string>? values) =>
        (values ?? [])
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Select(v => v.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
