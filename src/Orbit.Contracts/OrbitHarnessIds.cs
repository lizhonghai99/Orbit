namespace Orbit.Contracts;

/// <summary>
/// Shared Harness module id vocabulary for <c>agents/*.json</c> <c>harness</c>
/// and process env <c>ORBIT_HARNESS</c> (CSV).
/// </summary>
public static class OrbitHarnessIds
{
    public const string Mode = "mode";
    public const string Todo = "todo";
    public const string Skills = "skills";
    public const string FileAccess = "file_access";
    public const string FileMemory = "file_memory";
    public const string Background = "background";
    public const string Compaction = "compaction";
    public const string ToolApproval = "tool_approval";

    /// <summary>
    /// Default when manifest/env omit harness <b>or</b> list parses to empty:
    /// core providers + pipeline rails. Disk / Background still require explicit opt-in.
    /// </summary>
    /// <remarks>
    /// Contrast with <c>AgentBootstrap.CreateAgent(harness: new HashSet())</c>:
    /// an empty set passed in code means <b>enable nothing</b> (no default fill-in).
    /// Manifest <c>"harness": []</c> goes through <see cref="Parse"/> and becomes <see cref="Default"/>.
    /// </remarks>
    public static IReadOnlyList<string> Default { get; } =
        [Mode, Todo, Skills, Compaction, ToolApproval];

    public sealed record ParseResult(
        IReadOnlySet<string> Enabled,
        IReadOnlyList<string> Ignored);

    public static string ToEnvCsv(IEnumerable<string>? ids)
    {
        var parsed = Parse(ids);
        return string.Join(',', parsed.Enabled);
    }

    public static ParseResult Parse(IEnumerable<string>? ids)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignored = new List<string>();

        if (ids is not null)
        {
            foreach (var raw in ids)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var id = Normalize(raw);
                if (id is null)
                {
                    ignored.Add(raw.Trim());
                }
                else
                {
                    enabled.Add(id);
                }
            }
        }

        if (enabled.Count == 0)
        {
            foreach (var id in Default)
            {
                enabled.Add(id);
            }
        }

        return new ParseResult(enabled, ignored);
    }

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var id = raw.Trim().ToLowerInvariant().Replace('-', '_');
        return id switch
        {
            Mode or Todo or Skills or FileAccess or FileMemory or Compaction => id,
            "background" or "background_agents" => Background,
            "tool_approval" or "approval" => ToolApproval,
            _ => null
        };
    }
}
