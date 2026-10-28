using System.Collections.Frozen;
using System.Text;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orbit.Agents.Coder.Coder.Workflow;
using Orbit.Agents.Hosting;

namespace Orbit.Agents.Coder.Coder;

/// <summary>
/// FeatureList MAF Workflow: <c>feature_list.plan</c> → <c>feature_list.release_ready</c>.
/// Invoked by sub-agent <c>coder_planner</c> via tool <c>run_feature_list</c>.
/// Durable resume is the on-disk FeatureList (domain state), not a second graph-checkpoint layer —
/// Research uses <c>CheckpointManager</c> because its pipeline has no equivalent product store.
/// Coder AG-UI interrupt/resume stays on <c>Workflow.AsAIAgent</c> session serialize (MAF).
/// </summary>
public sealed class FeatureListWorkflow
{
    private const int DefaultMaxFeaturesPerRun = 2;
    private const int AbsoluteMaxFeaturesPerRun = 4;

    /// <summary>Max concurrent features in <c>ready</c> (WIP).</summary>
    public const int MaxReadyWip = 2;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Plan node chat client: no compaction / harness modules.</summary>
    private static readonly FrozenSet<string> PlanHarness =
        FrozenSet.ToFrozenSet(Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _agentId;
    private readonly string? _contentRoot;
    private readonly Func<IChatClient>? _chatFactory;
    private readonly ILoggerFactory? _loggerFactory;

    public FeatureListWorkflow(string agentId, string? contentRoot = null)
        : this(agentId, contentRoot, chatFactory: null, loggerFactory: null)
    {
    }

    public FeatureListWorkflow(
        string agentId,
        string? contentRoot,
        Func<IChatClient>? chatFactory,
        ILoggerFactory? loggerFactory)
    {
        _agentId = string.IsNullOrWhiteSpace(agentId) ? "coder" : agentId.Trim();
        _contentRoot = contentRoot;
        _chatFactory = chatFactory;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Agent-private root: <c>data/feature-lists/{agentId}/</c>.</summary>
    public string StoreRootHint => CoderFeatureStore.ResolveRoot(_agentId, _contentRoot);

    private CoderFeatureStore OpenStore() => CoderFeatureStore.ForAgent(_agentId, _contentRoot);

    public Task<string> RunAsync(
        string goal,
        string? constraints,
        CancellationToken cancellationToken) =>
        RunAsync(goal, constraints, feedback: null, DefaultMaxFeaturesPerRun, resume: false, reset: false, cancellationToken);

    public Task<string> RunAsync(
        string goal,
        string? constraints,
        string? feedback,
        int maxFeaturesPerRun,
        bool resume,
        CancellationToken cancellationToken) =>
        RunAsync(goal, constraints, feedback, maxFeaturesPerRun, resume, reset: false, cancellationToken);

    public async Task<string> RunAsync(
        string goal,
        string? constraints,
        string? feedback,
        int maxFeaturesPerRun,
        bool resume,
        bool reset,
        CancellationToken cancellationToken)
    {
        if (resume && reset)
        {
            return "Pass either resume=true or reset=true, not both.";
        }

        if (string.IsNullOrWhiteSpace(goal) && !resume)
        {
            return "goal is required (unless resume=true with an existing FeatureList).";
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return "A FeatureList Workflow is already running. Wait for it to finish or Stop the turn.";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        var ct = timeoutCts.Token;

        try
        {
            var store = OpenStore();
            if (resume && !store.Exists && string.IsNullOrWhiteSpace(goal))
            {
                return $"Nothing to resume (no FeatureList at `{store.FeatureListPath}`). Pass a goal to start a new one.";
            }

            if (!resume && !reset && store.Exists && store.TryLoad() is { } prior)
            {
                return
                    $"FeatureList already exists at `{store.FeatureListPath}` "
                    + $"({prior.Features.Count} features, goal: {prior.Goal}). "
                    + "Pass resume=true to release the next ready batch, or reset=true to archive and re-plan.";
            }

            string? resetBackup = null;
            if (reset && store.Exists)
            {
                resetBackup = store.ArchiveForReset();
            }

            var featureBudget = Math.Clamp(
                maxFeaturesPerRun <= 0 ? DefaultMaxFeaturesPerRun : maxFeaturesPerRun,
                1,
                AbsoluteMaxFeaturesPerRun);

            var chat = _chatFactory?.Invoke()
                       ?? ChatClientFactory.Create("coder-feature-list", _loggerFactory, PlanHarness);
            var log = new StringBuilder();
            log.AppendLine("# FeatureList Workflow");
            log.AppendLine($"Store: `{store.Root}`");
            if (!string.IsNullOrWhiteSpace(resetBackup))
            {
                log.AppendLine($"_Reset archived previous list to `{resetBackup}` (+ `.spec.md` if present)._");
            }

            log.AppendLine(
                $"Graph: feature_list.plan → feature_list.release_ready "
                + $"(up to {featureBudget} ready, WIP cap {MaxReadyWip}).");
            log.AppendLine("_Then run_coding_turn (fresh Generator+Evaluator sessions)._");
            log.AppendLine();

            CoderFeatureListDocument? existing = null;
            var skipPlan = false;
            if (resume && store.TryLoad() is { } loaded)
            {
                existing = loaded;
                skipPlan = true;
                if (!string.IsNullOrWhiteSpace(goal))
                {
                    existing.Goal = goal.Trim();
                }

                if (!string.IsNullOrWhiteSpace(constraints))
                {
                    existing.Constraints = constraints.Trim();
                }

                store.Save(existing);
            }

            var plan = new FeatureListPlanNode(chat);
            var release = new FeatureListReleaseReadyNode();
            var workflow = new WorkflowBuilder(plan)
                .AddEdge(plan, release)
                .WithOutputFrom(release)
                .Build();

            var initial = new FeatureListWorkflowState
            {
                Store = store,
                Goal = string.IsNullOrWhiteSpace(goal) ? (existing?.Goal ?? "") : goal.Trim(),
                Constraints = constraints,
                Feedback = feedback,
                FeatureBudget = featureBudget,
                SkipPlan = skipPlan,
                Document = existing,
                Log = log
            };

            await using var run = await InProcessExecution
                .RunStreamingAsync(workflow, initial, cancellationToken: ct)
                .ConfigureAwait(false);

            FeatureListWorkflowResult? outcome = null;
            await foreach (var evt in run.WatchStreamAsync().WithCancellation(ct).ConfigureAwait(false))
            {
                if (evt is WorkflowOutputEvent output && output.Data is FeatureListWorkflowResult o)
                {
                    outcome = o;
                }
                else if (evt is WorkflowErrorEvent err)
                {
                    throw err.Exception
                          ?? new InvalidOperationException(err.ToString() ?? "feature_list workflow error");
                }
            }

            return outcome?.Report ?? log + "\n_Workflow produced no outcome._";
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return
                "FeatureList Workflow timed out after 10 minutes. "
                + "Disk state may be partial — run_feature_list(resume=true).";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Long-Running: mark current <c>ready</c> (and continue <c>in_progress</c>) as the coding-turn scope;
    /// returns a brief that seeds a fresh Generator/Evaluator session.
    /// </summary>
    public CodingTurnClaim ClaimReadyForCodingTurn()
    {
        var store = OpenStore();
        var doc = store.TryLoad();
        if (doc is null)
        {
            return CodingTurnClaim.Fail(
                $"No FeatureList at `{store.FeatureListPath}` — run run_feature_list first.");
        }

        var dirty = false;
        var autoFailed = new List<string>();

        foreach (var f in doc.Features.Where(f => CoderFeatureStatus.IsInProgress(f.Status)).ToList())
        {
            f.InProgressClaims = Math.Max(0, f.InProgressClaims) + 1;
            dirty = true;
            if (f.InProgressClaims >= CoderFeatureStatus.MaxInProgressClaims
                || f.CodingTurnCount >= CoderFeatureStatus.MaxCodingTurns)
            {
                AutoFail(f, autoFailed,
                    f.CodingTurnCount >= CoderFeatureStatus.MaxCodingTurns
                        ? $"auto-failed: coding-turn budget exhausted ({f.CodingTurnCount}/{CoderFeatureStatus.MaxCodingTurns})."
                        : $"auto-failed: stuck in_progress across {f.InProgressClaims} coding turns "
                          + $"(max {CoderFeatureStatus.MaxInProgressClaims}).");
            }
        }

        var claimed = new List<string>();
        foreach (var f in doc.Features.Where(f => CoderFeatureStatus.IsReady(f.Status)).ToList())
        {
            if (f.CodingTurnCount >= CoderFeatureStatus.MaxCodingTurns)
            {
                AutoFail(f, autoFailed,
                    $"auto-failed: coding-turn budget exhausted ({f.CodingTurnCount}/{CoderFeatureStatus.MaxCodingTurns}).");
                dirty = true;
                continue;
            }

            f.Status = CoderFeatureStatus.InProgress;
            f.InProgressClaims = 1;
            f.CodingTurnCount = Math.Max(0, f.CodingTurnCount) + 1;
            claimed.Add(f.Id);
            dirty = true;
        }

        if (dirty)
        {
            store.Save(doc);
        }

        var inFlight = doc.Features
            .Where(f => CoderFeatureStatus.IsInProgress(f.Status))
            .Select(f => f.Id)
            .ToList();

        if (inFlight.Count == 0)
        {
            var extra = autoFailed.Count > 0
                ? $" Auto-failed: {string.Join(", ", autoFailed)}."
                : "";
            return CodingTurnClaim.Fail(
                "No ready/in_progress features to claim. "
                + "Call run_feature_list (or resume=true) to release a ready batch first."
                + extra);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Coding turn brief ({CoderAgentFactory.LongRunning})");
        if (autoFailed.Count > 0)
        {
            sb.AppendLine($"_Auto-failed: {string.Join(", ", autoFailed)}_");
            sb.AppendLine();
        }

        sb.AppendLine(CoderFeatureStore.Summarize(doc, store.Root));
        sb.AppendLine();
        sb.AppendLine("## This turn scope");
        foreach (var id in inFlight)
        {
            var f = doc.Features.First(x => x.Id == id);
            var remaining = Math.Max(0, CoderFeatureStatus.MaxCodingTurns - f.CodingTurnCount);
            sb.AppendLine(
                $"- `{f.Id}` — {f.Title} (codingTurns={f.CodingTurnCount}/{CoderFeatureStatus.MaxCodingTurns}, remaining={remaining})");
            foreach (var a in f.Acceptance.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                sb.AppendLine($"  acceptance: {a.Trim()}");
            }

            var verify = CoderFeatureStore.FormatVerifyPlan(f.Verify);
            if (!string.IsNullOrWhiteSpace(verify))
            {
                sb.AppendLine(verify);
            }

            if (!string.IsNullOrWhiteSpace(f.LastFindings))
            {
                sb.AppendLine("  lastFindings:");
                sb.AppendLine(f.LastFindings);
            }
        }

        return CodingTurnClaim.Success(sb.ToString(), inFlight);
    }

    private static void AutoFail(CoderFeatureItem f, List<string> autoFailed, string note)
    {
        f.Status = CoderFeatureStatus.Failed;
        f.Attempt = Math.Max(0, f.Attempt) + 1;
        f.VerifiedAt = null;
        f.LastFindings = string.IsNullOrWhiteSpace(f.LastFindings)
            ? note
            : f.LastFindings + "\n\n---\n" + note;
        autoFailed.Add(f.Id);
    }

    public string SetFeatureStatus(string featureId, string status, string? notes) =>
        SetFeatureStatus(featureId, status, notes, FeatureStatusActor.Any);

    public string SetFeatureStatus(
        string featureId,
        string status,
        string? notes,
        FeatureStatusActor actor)
    {
        var store = OpenStore();
        var doc = store.TryLoad();
        if (doc is null)
        {
            return $"No FeatureList at `{store.FeatureListPath}` — run run_feature_list first.";
        }

        var feature = doc.Features.FirstOrDefault(f =>
            string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase));
        if (feature is null)
        {
            return $"Unknown featureId '{featureId}'.";
        }

        var normalized = status.Trim().ToLowerInvariant();
        if (normalized is not (CoderFeatureStatus.Done or CoderFeatureStatus.Failed or CoderFeatureStatus.Pending
            or CoderFeatureStatus.Ready or CoderFeatureStatus.InProgress))
        {
            return "status must be pending|in_progress|ready|done|failed.";
        }

        if (actor == FeatureStatusActor.Generator)
        {
            if (normalized is not (CoderFeatureStatus.InProgress or CoderFeatureStatus.Pending))
            {
                return "generator may only set in_progress|pending (evaluator owns done/failed).";
            }

            // May not reopen done / re-touch unrelated pending rows.
            if (!CoderFeatureStatus.IsInProgress(feature.Status)
                && !CoderFeatureStatus.IsReady(feature.Status))
            {
                return
                    $"generator may only update ready|in_progress features (current={feature.Status}).";
            }
        }

        if (actor == FeatureStatusActor.Planner
            && normalized is not (CoderFeatureStatus.Pending or CoderFeatureStatus.Ready
                or CoderFeatureStatus.InProgress))
        {
            return "planner may only set pending|ready|in_progress (evaluator owns done/failed).";
        }

        if (normalized == CoderFeatureStatus.Done)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                return "done requires notes with VERDICT: PASS and verification evidence.";
            }

            if (!CoderFeatureStatus.NotesContainVerdictPass(notes))
            {
                return "done requires notes containing 'VERDICT: PASS' (evaluator contract).";
            }
        }

        feature.Status = normalized;
        if (normalized == CoderFeatureStatus.Done)
        {
            feature.VerifiedAt = DateTimeOffset.UtcNow;
            feature.InProgressClaims = 0;
        }
        else if (normalized == CoderFeatureStatus.Failed)
        {
            feature.Attempt = Math.Max(0, feature.Attempt) + 1;
            feature.VerifiedAt = null;
            feature.InProgressClaims = 0;
            if (feature.Attempt >= CoderFeatureStatus.MaxAttempts)
            {
                notes = (notes ?? "").Trim();
                var cap =
                    $"Attempt {feature.Attempt}/{CoderFeatureStatus.MaxAttempts} — will not auto-release again.";
                notes = string.IsNullOrWhiteSpace(notes) ? cap : notes + "\n" + cap;
            }
        }
        else if (normalized is CoderFeatureStatus.Pending or CoderFeatureStatus.Ready)
        {
            feature.VerifiedAt = null;
            feature.InProgressClaims = 0;
        }
        else if (normalized == CoderFeatureStatus.InProgress)
        {
            feature.VerifiedAt = null;
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            feature.LastFindings = string.IsNullOrWhiteSpace(feature.LastFindings)
                ? notes.Trim()
                : feature.LastFindings + "\n\n---\nnotes:\n" + notes.Trim();
        }

        store.Save(doc);
        return CoderFeatureStore.SummarizeUpdate(doc, feature, store.Root);
    }

    public string GetFeatureListSummary()
    {
        var store = OpenStore();
        var doc = store.TryLoad();
        return doc is null
            ? $"No FeatureList yet under `{store.Root}`."
            : CoderFeatureStore.Summarize(doc, store.Root);
    }
}

public enum FeatureStatusActor
{
    Any,
    Generator,
    Evaluator,
    Planner
}
