using System.Text.Json.Serialization;

namespace Orbit.Agents.Coder.Coder;

/// <summary>Disk-backed FeatureList for the Long-Running path (planner → generator → evaluator).</summary>
public sealed class CoderFeatureListDocument
{
    public string Goal { get; set; } = "";
    public string? Constraints { get; set; }
    public string SpecMarkdown { get; set; } = "";
    public IList<string> Stack { get; set; } = [];
    public IList<string> DesignPrinciples { get; set; } = [];
    public IList<CoderFeatureItem> Features { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CoderFeatureItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public IList<string> Acceptance { get; set; } = [];
    public IList<string> DependsOn { get; set; } = [];

    /// <summary>Concrete verify steps from Planner (commands / test targets) — not acceptance restated.</summary>
    public IList<string> Verify { get; set; } = [];

    /// <summary>pending | in_progress | ready | done | failed</summary>
    public string Status { get; set; } = CoderFeatureStatus.Pending;

    /// <summary>Markdown mirror of <see cref="Verify"/> (and optional notes) for Summarize / agents.</summary>
    public string? VerificationPlan { get; set; }

    public string? LastFindings { get; set; }

    /// <summary>How many times this feature was marked failed (retry signal).</summary>
    public int Attempt { get; set; }

    /// <summary>How many coding turns claimed this feature while already <c>in_progress</c>.</summary>
    public int InProgressClaims { get; set; }

    /// <summary>Total Long-Running coding turns that claimed this feature (lifetime budget).</summary>
    public int CodingTurnCount { get; set; }

    /// <summary>When status became <c>done</c> (audit: coder_evaluator PASS).</summary>
    public DateTimeOffset? VerifiedAt { get; set; }
}

public static class CoderFeatureStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Ready = "ready";
    public const string Done = "done";
    public const string Failed = "failed";

    /// <summary>Max failed→retry cycles before release_ready stops picking the feature.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Max coding-turn claims while stuck <c>in_progress</c> before auto-fail.</summary>
    public const int MaxInProgressClaims = 3;

    /// <summary>Max Long-Running coding turns per feature (Generator+Evaluator pairs).</summary>
    public const int MaxCodingTurns = 4;

    public static bool IsTerminalSuccess(string? status) =>
        string.Equals(status, Done, StringComparison.OrdinalIgnoreCase);

    public static bool IsReady(string? status) =>
        string.Equals(status, Ready, StringComparison.OrdinalIgnoreCase);

    public static bool IsInProgress(string? status) =>
        string.Equals(status, InProgress, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Eligible for release_ready (pending/failed only; respects <see cref="MaxAttempts"/>
    /// and lifetime <see cref="MaxCodingTurns"/>).
    /// </summary>
    public static bool IsOpen(string? status, int attempt = 0, int codingTurns = 0) =>
        (string.Equals(status, Pending, StringComparison.OrdinalIgnoreCase)
         || string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase))
        && attempt < MaxAttempts
        && codingTurns < MaxCodingTurns;

    public static bool NotesContainVerdictPass(string? notes) =>
        !string.IsNullOrWhiteSpace(notes)
        && notes.Contains("VERDICT: PASS", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Planner JSON payload (may omit SpecMarkdown — filled from prose).</summary>
public sealed class CoderPlannerPayload
{
    [JsonPropertyName("specMarkdown")]
    public string? SpecMarkdown { get; set; }

    [JsonPropertyName("stack")]
    public List<string>? Stack { get; set; }

    [JsonPropertyName("designPrinciples")]
    public List<string>? DesignPrinciples { get; set; }

    [JsonPropertyName("features")]
    public List<CoderPlannerFeature>? Features { get; set; }
}

public sealed class CoderPlannerFeature
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("acceptance")]
    public List<string>? Acceptance { get; set; }

    [JsonPropertyName("verify")]
    public List<string>? Verify { get; set; }

    [JsonPropertyName("dependsOn")]
    public List<string>? DependsOn { get; set; }
}
