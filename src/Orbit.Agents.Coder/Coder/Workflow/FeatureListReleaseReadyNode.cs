using System.Text;
using Microsoft.Agents.AI.Workflows;

namespace Orbit.Agents.Coder.Coder.Workflow;

/// <summary>Workflow node: pending/failed → ready (WIP cap); yield report for Generator.</summary>
public sealed class FeatureListReleaseReadyNode : Executor<FeatureListWorkflowState>
{
    public FeatureListReleaseReadyNode()
        : base("feature_list.release_ready")
    {
    }

    [YieldsOutput(typeof(FeatureListWorkflowResult))]
    public override async ValueTask HandleAsync(
        FeatureListWorkflowState state,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var doc = state.Document
                  ?? state.Store.TryLoad()
                  ?? throw new InvalidOperationException("No FeatureList document for release_ready.");

        state.Log.AppendLine("## FeatureList (before release)");
        state.Log.AppendLine();
        state.Log.AppendLine("```");
        state.Log.Append(CoderFeatureStore.Summarize(doc, state.Store.Root));
        state.Log.AppendLine("```");
        state.Log.AppendLine();

        var released = ReleaseReadyBatch(doc, state.Store, state.FeatureBudget, state.Feedback, state.Log);

        state.Log.AppendLine("## Next");
        state.Log.AppendLine();
        state.Log.AppendLine($"1. FeatureList: `{state.Store.Root}`");
        if (released > 0)
        {
            state.Log.AppendLine("2. Planner `run_coding_turn` — fresh Generator+Evaluator sessions.");
            state.Log.AppendLine("3. More pending → `run_feature_list(resume=true)` then another coding turn.");
        }
        else
        {
            state.Log.AppendLine(
                "2. No new ready items — WIP full, blocked, Attempt cap, done, or in_progress.");
            state.Log.AppendLine("3. Finish in-flight coding turns, or free a ready slot.");
        }

        state.Log.AppendLine();
        state.Log.AppendLine("```");
        state.Log.Append(CoderFeatureStore.Summarize(doc, state.Store.Root));
        state.Log.AppendLine("```");

        await context.YieldOutputAsync(
                new FeatureListWorkflowResult
                {
                    Report = state.Log.ToString(),
                    ReleasedReadyCount = released
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static int ReleaseReadyBatch(
        CoderFeatureListDocument doc,
        CoderFeatureStore store,
        int budget,
        string? resumeFeedback,
        StringBuilder sb)
    {
        var released = 0;
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readyCount = CoderFeatureStore.CountReady(doc);
        var slots = Math.Max(0, FeatureListWorkflow.MaxReadyWip - readyCount);
        var limit = Math.Min(budget, slots);

        sb.AppendLine("## feature_list.release_ready");
        sb.AppendLine();
        sb.AppendLine($"_Ready WIP: {readyCount}/{FeatureListWorkflow.MaxReadyWip}; this batch limit: {limit}._");
        sb.AppendLine();

        if (limit == 0)
        {
            sb.AppendLine(
                readyCount >= FeatureListWorkflow.MaxReadyWip
                    ? "_No slots — finish or demote existing ready / in_progress first._"
                    : "_Batch limit is 0._");
            sb.AppendLine();
            return 0;
        }

        while (released < limit)
        {
            var feature = CoderFeatureStore.PickNext(doc, attempted);
            if (feature is null)
            {
                if (released == 0)
                {
                    sb.AppendLine(
                        "_No open features (pending/failed with deps satisfied, "
                        + $"Attempt < {CoderFeatureStatus.MaxAttempts}, "
                        + $"codingTurns < {CoderFeatureStatus.MaxCodingTurns})._");
                }

                break;
            }

            attempted.Add(feature.Id);
            var hadFailureContext = !string.IsNullOrWhiteSpace(feature.LastFindings);
            feature.Status = CoderFeatureStatus.Ready;
            feature.VerificationPlan = CoderFeatureStore.FormatVerifyPlan(feature.Verify)
                                       ?? feature.VerificationPlan;
            // Intentional: same resumeFeedback text is appended to every feature released in
            // this batch (Planner feedback is batch-scoped, not per-feature).
            if (!string.IsNullOrWhiteSpace(resumeFeedback))
            {
                var note = resumeFeedback.Trim();
                feature.LastFindings = string.IsNullOrWhiteSpace(feature.LastFindings)
                    ? "Resume feedback:\n" + note
                    : feature.LastFindings + "\n\n---\nResume feedback:\n" + note;
            }

            released++;
            store.Save(doc);

            sb.AppendLine($"- **ready** `{feature.Id}` — {feature.Title}"
                          + (hadFailureContext ? " _(retry; see lastFindings)_" : ""));
            if (feature.Verify.Count > 0)
            {
                sb.AppendLine($"  Verify: {string.Join("; ", feature.Verify)}");
            }
            else if (feature.Acceptance.Count > 0)
            {
                sb.AppendLine(
                    $"  Acceptance (criteria only — prefer plan verify[]): {string.Join("; ", feature.Acceptance)}");
            }
        }

        sb.AppendLine();
        return released;
    }
}
