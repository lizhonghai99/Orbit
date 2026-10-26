using System.Security.Cryptography;
using System.Text;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Per-agent Skills under project <c>skills/*/SKILL.md</c>, <c>agents/{id}/skills</c>,
/// or user overrides under <c>{ORBIT_ROOT}/data/skills/{id}</c>.
/// </summary>
public static class SkillCatalog
{
    public static string? ResolveSkillsDirectory(string agentId, string? agentContentRoot = null)
    {
        foreach (var root in EnumerateSkillRoots(agentId, agentContentRoot))
        {
            if (Directory.Exists(root)
                && Directory.EnumerateDirectories(root).Any(d => File.Exists(Path.Combine(d, "SKILL.md"))))
            {
                return root;
            }
        }

        return null;
    }

    public static IReadOnlyList<SkillInfo> Discover(string agentId, string? agentContentRoot = null)
    {
        var found = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateSkillRoots(agentId, agentContentRoot))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillFile))
                {
                    continue;
                }

                var id = Path.GetFileName(dir);
                if (found.ContainsKey(id))
                {
                    continue;
                }

                var (name, description) = ReadFrontMatter(skillFile);
                found[id] = new SkillInfo(id, name ?? id, description, skillFile);
            }
        }

        return found.Values.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<SkillSummaryDto> ToSummaries(IEnumerable<SkillInfo> skills)
    {
        var repoRoot = OrbitPathNames.TryGetOrbitRoot();
        return skills.Select(s => ToSummary(s, repoRoot)).ToList();
    }

    public static SkillSummaryDto ToSummary(SkillInfo skill, string? repoRoot = null)
    {
        repoRoot ??= OrbitPathNames.TryGetOrbitRoot();
        var dir = Path.GetDirectoryName(skill.Path) ?? "";
        return new SkillSummaryDto
        {
            Id = skill.Id,
            Name = skill.Name,
            Description = skill.Description,
            Source = ClassifySource(skill.Path, repoRoot),
            Enabled = true,
            HasScripts = DetectScripts(dir),
            Sha256 = ComputeSha256Hex(skill.Path),
            Path = skill.Path
        };
    }

    private static IEnumerable<string> EnumerateSkillRoots(string agentId, string? agentContentRoot)
    {
        // First existing root wins for ResolveSkillsDirectory; Discover first-wins per skill id.
        // User overrides must come first so "visible in /skills" matches load_skill injection root.
        var repoRoot = OrbitPathNames.TryGetOrbitRoot();
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            yield return Path.Combine(repoRoot, "data", "skills", agentId);
        }

        if (!string.IsNullOrWhiteSpace(agentContentRoot))
        {
            yield return Path.Combine(agentContentRoot, "skills");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "skills");

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            yield return Path.Combine(repoRoot, "agents", agentId, "skills");
            yield return Path.Combine(repoRoot, "src", $"Orbit.Agents.{ToPascal(agentId)}", "skills");
        }
    }

    internal static string ClassifySource(string skillPath, string? repoRoot)
    {
        try
        {
            var full = Path.GetFullPath(skillPath);
            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                var userRoot = Path.GetFullPath(Path.Combine(repoRoot, "data", "skills"));
                if (OrbitPathNames.IsUnder(full, userRoot))
                {
                    return "user";
                }
            }

            return "builtin";
        }
        catch
        {
            return "builtin";
        }
    }

    private static bool DetectScripts(string skillDir)
    {
        try
        {
            var scripts = Path.Combine(skillDir, "scripts");
            if (Directory.Exists(scripts)
                && Directory.EnumerateFileSystemEntries(scripts).Any())
            {
                return true;
            }

            return Directory.EnumerateFiles(skillDir, "*.*", SearchOption.TopDirectoryOnly)
                .Any(f =>
                {
                    var ext = Path.GetExtension(f);
                    return ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".sh", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".py", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch
        {
            return false;
        }
    }

    private static string? ComputeSha256Hex(string skillFile)
    {
        try
        {
            var bytes = File.ReadAllBytes(skillFile);
            var hash = SHA256.HashData(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string ToPascal(string agentId) =>
        string.IsNullOrEmpty(agentId)
            ? agentId
            : char.ToUpperInvariant(agentId[0]) + agentId[1..];

    private static (string? Name, string? Description) ReadFrontMatter(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                return (null, null);
            }

            string? name = null;
            string? description = null;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    break;
                }

                var line = lines[i];
                var idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim().Trim('"');
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    name = value;
                }
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                {
                    description = value;
                }
            }

            return (name, description);
        }
        catch
        {
            return (null, null);
        }
    }
}

public sealed record SkillInfo(string Id, string Name, string? Description, string Path);
