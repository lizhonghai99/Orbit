using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Orbit.Agents.Coder.Coder;

/// <summary>
/// Long-Running coding turn with <b>context reset</b>: each call runs Generator then Evaluator
/// on a brand-new <see cref="AgentSession"/>, seeded only from FeatureList (disk) — not prior chat.
/// Lives entirely in the Coder process (nested agents); outer AG-UI history only sees this tool's summary.
/// </summary>
public sealed class LongRunningCodingTurn
{
    public const string ToolName = "run_coding_turn";

    /// <summary>Wall-clock budget for one Generator+Evaluator pair (linked CTS).</summary>
    public static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(15);

    private readonly FeatureListWorkflow _featureList;
    private readonly AIAgent _generator;
    private readonly AIAgent _evaluator;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LongRunningCodingTurn(
        FeatureListWorkflow featureList,
        AIAgent generator,
        AIAgent evaluator,
        ILoggerFactory? loggerFactory = null)
    {
        _featureList = featureList;
        _generator = generator;
        _evaluator = evaluator;
        _log = loggerFactory?.CreateLogger(typeof(LongRunningCodingTurn));
    }

    /// <summary>
    /// Claim <c>ready</c> → <c>in_progress</c>, then fresh Generator session → fresh Evaluator session.
    /// </summary>
    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return "A coding turn is already running. Wait for it to finish.";
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TurnTimeout);
        var ct = timeoutCts.Token;

        try
        {
            var claim = _featureList.ClaimReadyForCodingTurn();
            if (!claim.Ok)
            {
                return claim.Message;
            }

            var brief = claim.Message;
            _log?.LogInformation("Long-Running coding turn start ({Count} feature(s))", claim.ClaimedIds.Count);

            var genText = await RunFreshAsync(
                    _generator,
                    """
                    CONTEXT RESET — Long-Running coding turn.
                    Ignore any prior chat; the FeatureList brief below is the only task scope.

                    """ + brief + """

                    Implement the in_progress / listed features in the Workspace.
                    Prefer minimal diffs (load_skill minimal-diff when useful).
                    Do NOT mark features done — Evaluator owns PASS→done.
                    When implementation is ready for QA, summarize what you changed and which feature ids.
                    """,
                    ct)
                .ConfigureAwait(false);

            var evalText = await RunFreshAsync(
                    _evaluator,
                    """
                    CONTEXT RESET — Long-Running evaluation turn.
                    Ignore any prior chat. Grade only against FeatureList + Generator report.

                    ## FeatureList brief
                    """ + brief + """

                    ## Generator report
                    """ + genText + """

                    Run verify[] / acceptance checks via bash for real. Read workspace files when needed.
                    set_feature_status(done|failed) with VERDICT: PASS or VERDICT: FAIL in notes.
                    done notes MUST contain the exact substring VERDICT: PASS.
                    """,
                    ct)
                .ConfigureAwait(false);

            // Full report → planner tool_result / session JSONL. Compaction owns model context size.
            var sb = new StringBuilder();
            sb.AppendLine("# Long-Running coding turn (fresh sessions)");
            sb.AppendLine($"Claimed: {string.Join(", ", claim.ClaimedIds)}");
            sb.AppendLine();
            sb.AppendLine("## Generator");
            sb.AppendLine(genText);
            sb.AppendLine();
            sb.AppendLine("## Evaluator");
            sb.AppendLine(evalText);
            sb.AppendLine();
            sb.AppendLine("## FeatureList now");
            sb.AppendLine(_featureList.GetFeatureListSummary());
            sb.AppendLine();
            sb.AppendLine(
                "Next: if pending/failed remain → run_feature_list(resume=true) then run_coding_turn again "
                + "(each call is a new context reset). If all done → handoff coder_router.");
            return sb.ToString();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            return
                $"Coding turn timed out after {TurnTimeout.TotalMinutes:0} minutes. "
                + "FeatureList may be partial (in_progress). Call run_coding_turn again or set_feature_status(failed).";
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> RunFreshAsync(
        AIAgent agent,
        string userText,
        CancellationToken cancellationToken)
    {
        var session = await agent.CreateSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var response = await agent.RunAsync(userText, session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Text ?? "";
    }
}

/// <summary>Result of claiming ready features for a coding turn.</summary>
public readonly record struct CodingTurnClaim(bool Ok, string Message, IReadOnlyList<string> ClaimedIds)
{
    public static CodingTurnClaim Fail(string message) =>
        new(false, message, Array.Empty<string>());

    public static CodingTurnClaim Success(string brief, IReadOnlyList<string> ids) =>
        new(true, brief, ids);
}
