namespace Orbit.Contracts;

/// <summary>Approval card <c>source</c> values on session JSONL payloads.</summary>
public static class ApprovalSources
{
    /// <summary>Tool-approval interrupt (Hosting → Runtime).</summary>
    public const string Tool = "tool";

    /// <summary>True for current and legacy tool-approval cards.</summary>
    public static bool IsToolInterrupt(string? source) =>
        string.Equals(source, Tool, StringComparison.OrdinalIgnoreCase)
        // Legacy sessions wrote "maf" before the neutral marker.
        || string.Equals(source, "maf", StringComparison.OrdinalIgnoreCase);
}
