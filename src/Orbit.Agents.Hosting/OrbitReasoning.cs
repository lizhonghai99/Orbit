using Microsoft.Extensions.AI;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Maps <c>ORBIT_REASONING_ENABLED</c> / <c>ORBIT_REASONING_EFFORT</c> onto MEAI
/// <see cref="ChatOptions.Reasoning"/>. Unset env → provider default (<see langword="null"/>).
/// </summary>
public static class OrbitReasoning
{
    public const string EnabledKey = "ORBIT_REASONING_ENABLED";
    public const string EffortKey = "ORBIT_REASONING_EFFORT";

    /// <summary>Stable label for <c>/health</c> / capabilities: default | none | low | medium | high | extrahigh.</summary>
    public static string DescribeForHealth()
    {
        var opts = ResolveOptions();
        if (opts is null)
        {
            return "default";
        }

        return opts.Effort switch
        {
            ReasoningEffort.None => "none",
            ReasoningEffort.Low => "low",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "extrahigh",
            _ => "medium"
        };
    }

    public static ReasoningOptions? ResolveOptions()
    {
        var enabled = GetEnv(EnabledKey)?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(enabled))
        {
            return null;
        }

        if (IsOff(enabled))
        {
            return new ReasoningOptions
            {
                Effort = ReasoningEffort.None,
                Output = ReasoningOutput.None
            };
        }

        if (!IsOn(enabled))
        {
            return null;
        }

        return new ReasoningOptions
        {
            Effort = ParseEffort(GetEnv(EffortKey)),
            Output = ReasoningOutput.Full
        };
    }

    public static ReasoningEffort ParseEffort(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "high" => ReasoningEffort.High,
            "extrahigh" or "extra_high" or "xhigh" => ReasoningEffort.ExtraHigh,
            _ => ReasoningEffort.Medium
        };

    private static bool IsOff(string value) =>
        value is "0" or "false" or "off" or "no" or "none";

    private static bool IsOn(string value) =>
        value is "1" or "true" or "on" or "yes";

    private static string? GetEnv(string name) => Orbit.Contracts.OrbitEnv.Get(name);
}
