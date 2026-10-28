using System.Text;

namespace Orbit.Agents.Coder.Coder.Workflow;

/// <summary>State flowing through FeatureList Workflow: Plan → ReleaseReady.</summary>
public sealed class FeatureListWorkflowState
{
    public required CoderFeatureStore Store { get; init; }
    public required string Goal { get; set; }
    public string? Constraints { get; set; }
    public string? Feedback { get; set; }
    public int FeatureBudget { get; init; }
    public bool SkipPlan { get; init; }
    public CoderFeatureListDocument? Document { get; set; }
    public required StringBuilder Log { get; init; }
}

/// <summary>Result yielded by <see cref="FeatureListReleaseReadyNode"/>.</summary>
public sealed class FeatureListWorkflowResult
{
    public required string Report { get; init; }
    public int ReleasedReadyCount { get; init; }
}
