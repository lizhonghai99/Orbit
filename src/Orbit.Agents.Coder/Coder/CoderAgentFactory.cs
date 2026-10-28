using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orbit.Agents.Hosting;
using Orbit.Contracts;

namespace Orbit.Agents.Coder.Coder;

/// <summary>
/// Builds the AG-UI <b>Handoff</b> host. Externally one Coder; internally two paths:
/// <list type="bullet">
/// <item><b>Short-Running</b>: <c>coder_router</c> → <c>coder_generator</c> → <c>coder_evaluator</c> (HITL handoff)</item>
/// <item><b>Long-Running</b>: <c>coder_router</c> → <c>coder_planner</c>; each <c>run_coding_turn</c> is a
/// context reset (fresh Generator session → fresh Evaluator session), seeded from FeatureList only</item>
/// </list>
/// </summary>
public static class CoderAgentFactory
{
    /// <summary>Route name: single-shot implement + end-of-run QA (no FeatureList Workflow).</summary>
    public const string ShortRunning = "Short-Running";

    /// <summary>Route name: FeatureList plan/release then fresh coding turns.</summary>
    public const string LongRunning = "Long-Running";

    /// <summary>Soft cap on Short-Running conversation size (termination when no handoff).</summary>
    public const int ShortRunningMaxMessages = 80;

    public const string RouterId = "coder_router";
    public const string PlannerId = "coder_planner";
    public const string GeneratorId = "coder_generator";
    public const string EvaluatorId = "coder_evaluator";

    public static AIAgent Create(
        string agentId,
        string displayName,
        CoderShellTools shell,
        FeatureListWorkflow featureList,
        ILoggerFactory? loggerFactory = null,
        string? contentRoot = null)
    {
        // Nested Long-Running turns: autonomous (no Shell approval cards) + CreateSessionAsync per turn.
        // Share FileMemory under the host agent id (e.g. data/file-memory/coder/), not per handoff id.
        var memoryId = string.IsNullOrWhiteSpace(agentId) ? "coder" : agentId.Trim();

        var turnGenerator = CreateGenerator(
            shell, featureList, loggerFactory, contentRoot, autonomous: true, fileMemoryAgentId: memoryId);
        var turnEvaluator = CreateEvaluator(
            shell, featureList, loggerFactory, contentRoot, autonomous: true, fileMemoryAgentId: memoryId);
        var codingTurn = new LongRunningCodingTurn(
            featureList, turnGenerator, turnEvaluator, loggerFactory);

        // Short-Running handoff participants keep HITL (ApprovalRequired + tool_approval).
        var generator = CreateGenerator(
            shell, featureList, loggerFactory, contentRoot, autonomous: false, fileMemoryAgentId: memoryId);
        var evaluator = CreateEvaluator(
            shell, featureList, loggerFactory, contentRoot, autonomous: false, fileMemoryAgentId: memoryId);
        var planner = CreatePlanner(featureList, codingTurn, loggerFactory);
        var router = CreateRouter(loggerFactory);

        // Termination ASSUMPTION: `messages` is this handoff-run transcript, not the whole
        // multi-turn AG-UI session. If MAF ever passes cumulative session history, long chats
        // would stop early — verify on first real multi-turn run.
        // FailGate is advisory (cannot block handoff tools); ~80 messages is the hard-ish outer cap.
        var handoff = AgentWorkflowBuilder.CreateHandoffBuilderWith(router)
            .WithHandoffs(router, [planner, generator])
            .WithHandoffs(planner, [router])
            .WithHandoffs(generator, [evaluator, router])
            .WithHandoffs(evaluator, [generator, router])
            .WithTerminationCondition(static messages => messages.Count >= ShortRunningMaxMessages)
            .Build();

        return handoff.AsAIAgent(
            id: agentId,
            name: displayName,
            description:
            "Coder: Short-Running (router→generator→evaluator HITL) or Long-Running "
            + "(router→planner; run_coding_turn = fresh Generator+Evaluator sessions).");
    }

    private static AIAgent CreateRouter(ILoggerFactory? loggerFactory) =>
        AgentBootstrap.CreateAgent(
            agentId: RouterId,
            displayName: "Coder Router",
            instructions:
            """
            You are coder_router — intent router ONLY.
            You do NOT edit files, run bash, plan FeatureLists, or grade work.

            Two routes (always hand off; never implement yourself):
            - Short-Running (bounded edits) → coder_generator
            - Long-Running (multi-feature / build-from-prompt) → coder_planner
            - Ambiguous → ask_user_choice or ask_user_confirm once, then handoff.

            Never answer coding work in your own voice after a handoff target is clear.
            Match the user's language for any brief routing note.
            """,
            tools: [],
            loggerFactory: loggerFactory,
            harness: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                OrbitHarnessIds.ToolApproval
            });

    private static AIAgent CreatePlanner(
        FeatureListWorkflow featureList,
        LongRunningCodingTurn codingTurn,
        ILoggerFactory? loggerFactory)
    {
        Task<string> RunFeatureList(
            string goal,
            string? constraints = null,
            string? feedback = null,
            int maxFeaturesPerRun = 2,
            bool resume = false,
            bool reset = false,
            CancellationToken ct = default) =>
            featureList.RunAsync(goal, constraints, feedback, maxFeaturesPerRun, resume, reset, ct);

        var runFeatureListTool = AIFunctionFactory.Create(
            RunFeatureList,
            name: "run_feature_list",
            description:
            "Run FeatureList Workflow (feature_list.plan → feature_list.release_ready). "
            + "If a list already exists, pass resume=true (next batch) or reset=true "
            + "(archives old list then re-plans — confirm with user first). "
            + $"WIP ≤ {FeatureListWorkflow.MaxReadyWip}. Then call run_coding_turn.");

        var runCodingTurnTool = AIFunctionFactory.Create(
            (CancellationToken ct) => codingTurn.RunAsync(ct),
            name: LongRunningCodingTurn.ToolName,
            description:
            "Long-Running context reset: claim ready features, run Generator on a NEW AgentSession, "
            + "then Evaluator on another NEW AgentSession. Seeded only from FeatureList. "
            + $"Budget {LongRunningCodingTurn.TurnTimeout.TotalMinutes:0} minutes. Requires approval.");

        var tools = new List<AITool>
        {
            runFeatureListTool,
            // Entry gate: user confirms before unattended write + bash inside the coding turn.
            new ApprovalRequiredAIFunction(runCodingTurnTool),
            AIFunctionFactory.Create(
                () => featureList.GetFeatureListSummary(),
                name: "get_feature_list",
                description: "Summarize FeatureList (status, verify, lastFindings)."),
            AIFunctionFactory.Create(
                (string featureId, string status, string? notes = null) =>
                    featureList.SetFeatureStatus(featureId, status, notes, FeatureStatusActor.Planner),
                name: "set_feature_status",
                description:
                "Planner-only: pending|ready|in_progress. Evaluator owns done/failed."),
        };

        return AgentBootstrap.CreateAgent(
            agentId: PlannerId,
            displayName: "Planner",
            instructions:
            """
            You are coder_planner on the Long-Running path (Anthropic Planner).
            You do NOT edit the Workspace and do NOT hand off to coder_generator.

            Loop:
            1) run_feature_list(goal) for new work.
               If a list already exists → resume=true for next batch.
               reset=true archives the old list then re-plans — ask_user_confirm first; never reset unilaterally.
               Each feature needs acceptance + verify[].
            2) run_coding_turn — CONTEXT RESET (user approval card first): fresh Generator then Evaluator sessions.
            3) Read the tool summary / get_feature_list (codingTurns remaining budget).
               - FAIL or more pending → resume=true (if needed) then run_coding_turn again.
               - Features at Attempt / codingTurns cap will not re-release — tell the user and stop that feature.
               - All done → handoff coder_router.
            4) Never implement or grade yourself. set_feature_status may only set pending|ready|in_progress.

            Match the user's language for brief status notes.
            """,
            tools,
            loggerFactory: loggerFactory,
            harness: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                OrbitHarnessIds.ToolApproval
            });
    }

    private static AIAgent CreateGenerator(
        CoderShellTools shell,
        FeatureListWorkflow featureList,
        ILoggerFactory? loggerFactory,
        string? contentRoot,
        bool autonomous,
        string? fileMemoryAgentId = null)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => $"Coder workspace: {shell.WorkspaceRoot}",
                name: "get_coder_workspace",
                description: "Return the on-disk workspace root for this run."),
            WrapBash(shell, autonomous),
            AIFunctionFactory.Create(
                () => featureList.GetFeatureListSummary(),
                name: "get_feature_list",
                description: "Summarize FeatureList including verify[] and evaluator lastFindings."),
            AIFunctionFactory.Create(
                (string featureId, string status, string? notes = null) =>
                    featureList.SetFeatureStatus(featureId, status, notes, FeatureStatusActor.Generator),
                name: "set_feature_status",
                description:
                "Only ready|in_progress features → in_progress|pending. Do NOT mark done/failed."),
        };

        var instructions = autonomous
            ? """
              You are coder_generator on a Long-Running coding turn (context reset).
              Implement in the Workspace via file_access_* and bash. Prefer minimal diffs (load_skill minimal-diff).
              Scope is ONLY the FeatureList brief / in_progress items in this turn's user message.
              set_feature_status may only touch ready|in_progress rows (in_progress|pending). Do NOT mark done.
              Summarize changes + feature ids when ready for Evaluator.
              """
            : """
              You are coder_generator (Anthropic Generator) on Short-Running.
              Implement in the Workspace via file_access_* and bash. Prefer minimal diffs (load_skill minimal-diff).

              Short-Running (from coder_router): implement, then handoff coder_evaluator for QA.
              On evaluator FAIL: fix lastFindings, then handoff coder_evaluator again.
              If evaluator says generator handoffs are blocked, handoff coder_router (do not keep patching).
              Reply in the user's language for brief status notes.
              """;

        var harness = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OrbitHarnessIds.Mode,
            OrbitHarnessIds.Todo,
            OrbitHarnessIds.FileMemory,
            OrbitHarnessIds.FileAccess,
            OrbitHarnessIds.Skills,
            OrbitHarnessIds.Compaction
        };
        if (!autonomous)
        {
            harness.Add(OrbitHarnessIds.ToolApproval);
        }

        return AgentBootstrap.CreateAgent(
            agentId: autonomous ? GeneratorId + "_turn" : GeneratorId,
            displayName: autonomous ? "Generator (coding turn)" : "Generator",
            instructions: instructions,
            tools,
            contentRoot: contentRoot,
            loggerFactory: loggerFactory,
            harness: harness,
            includeAskUser: !autonomous,
            fileMemoryAgentId: fileMemoryAgentId);
    }

    private static AIAgent CreateEvaluator(
        CoderShellTools shell,
        FeatureListWorkflow featureList,
        ILoggerFactory? loggerFactory,
        string? contentRoot,
        bool autonomous,
        string? fileMemoryAgentId = null)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => $"Coder workspace: {shell.WorkspaceRoot}",
                name: "get_coder_workspace",
                description: "Workspace root for verification commands."),
            WrapBash(shell, autonomous),
            AIFunctionFactory.Create(
                () => featureList.GetFeatureListSummary(),
                name: "get_feature_list",
                description: "Read features, acceptance, verify[], prior findings."),
            AIFunctionFactory.Create(
                (string featureId, string status, string? notes = null) =>
                    featureList.SetFeatureStatus(featureId, status, notes, FeatureStatusActor.Evaluator),
                name: "set_feature_status",
                description: "After grading: done (notes MUST include VERDICT: PASS) or failed (VERDICT: FAIL + findings)."),
        };

        if (!autonomous)
        {
            tools.Add(AIFunctionFactory.Create(
                ShortRunningFailGate.Status,
                name: "short_fail_status",
                description: "How many FAIL→generator cycles remain for this session."));
            tools.Add(AIFunctionFactory.Create(
                ShortRunningFailGate.NoteFail,
                name: "note_short_fail",
                description:
                "REQUIRED before handoff to coder_generator on VERDICT FAIL. "
                + "Returns whether another generator pass is allowed."));
            tools.Add(AIFunctionFactory.Create(
                ShortRunningFailGate.NotePass,
                name: "note_short_pass",
                description: "Call on VERDICT PASS before handoff coder_router (clears fail cycle)."));
            tools.Add(AIFunctionFactory.Create(
                ShortRunningFailGate.ResetAfterUserOk,
                name: "reset_short_fail_gate",
                description: "Only after ask_user_confirm approved another repair round past the cap."));
        }

        var instructions = autonomous
            ? """
              You are coder_evaluator on a Long-Running evaluation turn (context reset).
              You do NOT implement features. Grade only this turn's FeatureList scope + Generator report.
              Use file_access_* (read-only) and bash. Notes must include VERDICT: PASS or VERDICT: FAIL.
              set_feature_status(done) requires the exact substring VERDICT: PASS in notes.
              Do not hand off — the coding-turn tool returns to Planner.
              """
            : """
              You are coder_evaluator (Anthropic Evaluator) — Short-Running end-of-run QA. You do NOT implement features.

              1) Scope the Short-Running work Generator described (use file_access_* read-only when helpful).
              2) Run checks via bash for real.
              3) Grade vs the user's acceptance — not only exit code 0.
              4) Notes must include VERDICT: PASS or VERDICT: FAIL + evidence; on FAIL actionable findings.
              5) Handoff discipline (HARD gate via tools):
                 - On FAIL: ALWAYS call note_short_fail FIRST. Only handoff coder_generator if it says you may.
                   If blocked → ask_user_confirm; on approve call reset_short_fail_gate then generator;
                   on reject/unclear → coder_router.
                 - On PASS: note_short_pass then handoff coder_router.

              Never mark success without real checks. Match the user's language for the summary.
              """;

        var harness = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OrbitHarnessIds.FileAccess
        };
        if (!autonomous)
        {
            harness.Add(OrbitHarnessIds.ToolApproval);
        }

        return AgentBootstrap.CreateAgent(
            agentId: autonomous ? EvaluatorId + "_turn" : EvaluatorId,
            displayName: autonomous ? "Evaluator (coding turn)" : "Evaluator",
            instructions: instructions,
            tools,
            contentRoot: contentRoot,
            loggerFactory: loggerFactory,
            harness: harness,
            includeAskUser: !autonomous,
            fileAccessReadOnly: true,
            fileMemoryAgentId: fileMemoryAgentId);
    }

    private static AITool WrapBash(CoderShellTools shell, bool autonomous)
    {
        var fn = AIFunctionFactory.Create(
            shell.RunBashAsync,
            name: "bash",
            description: autonomous
                ? "Run a shell command in the Coder workspace (Git Bash). Autonomous Long-Running turn — no approval card."
                : "Run a shell command in the Coder workspace (Git Bash). Builds/tests/git. Requires approval.");
        return autonomous ? fn : new ApprovalRequiredAIFunction(fn);
    }
}
