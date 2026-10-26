using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Orbit.Agents.Hosting;
using Orbit.Contracts;

namespace Orbit.Agents.Research.Research;

/// <summary>
/// Sequential Workflow: Scope → Outline → Draft.
/// Invoked from the AG-UI outer agent via <c>run_research_pipeline</c> (not as the AG-UI host itself).
/// </summary>
public sealed class ResearchPipeline
{
    /// <summary>Stable session id so <c>resume=true</c> can find the latest checkpoint.</summary>
    public const string PipelineSessionId = "orbit-research";

    private const int MaxCheckpointFiles = 40;

    /// <summary>Hard ceiling so a stuck Scope/Outline/Draft cannot hold the gate forever.</summary>
    private static readonly TimeSpan PipelineTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _checkpointRoot;
    private readonly Func<string, CancellationToken, Task>? _onStage;

    public ResearchPipeline(string agentId, string? contentRoot = null)
        : this(agentId, contentRoot, onStage: null)
    {
    }

    public ResearchPipeline(
        string agentId,
        string? contentRoot,
        Func<string, CancellationToken, Task>? onStage)
    {
        var root = OrbitPathNames.ResolveDataRoot(contentRoot);
        _checkpointRoot = Path.GetFullPath(
            Path.Combine(root, "data", "checkpoints", OrbitPathNames.SanitizeFileName(agentId)));
        Directory.CreateDirectory(_checkpointRoot);
        _onStage = onStage;
    }

    public string CheckpointRoot => _checkpointRoot;

    public Task<string> RunAsync(
        string topic,
        string? focus,
        string? evidence,
        CancellationToken cancellationToken) =>
        RunAsync(topic, focus, evidence, resume: false, cancellationToken);

    public async Task<string> RunAsync(
        string topic,
        string? focus,
        string? evidence,
        bool resume,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return "topic is required.";
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return "A research pipeline is already running. Wait for it to finish or Stop the turn.";
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(PipelineTimeout);
            var ct = timeoutCts.Token;

            PruneCheckpointFiles();

            var chat = ChatClientFactory.Create("research-pipeline");
            var focusLine = string.IsNullOrWhiteSpace(focus)
                ? ""
                : $" Focus area: {focus.Trim()}.";
            var hasEvidence = !string.IsNullOrWhiteSpace(evidence);

            var scope = new ChatClientAgent(
                chat,
                instructions:
                "You are a research scoping specialist. Given a topic"
                + (hasEvidence ? " and provided evidence notes" : "")
                + ", clarify the question, list 3–5 sub-questions, "
                + "and note assumptions / what still needs primary sources. Output structured Markdown only. "
                + "Do not draft the final brief yet. Do not invent facts or URLs.",
                name: "Scope");

            var outline = new ChatClientAgent(
                chat,
                instructions:
                "You are a research outline editor. Based on the scoping notes"
                + (hasEvidence ? " and evidence" : "")
                + ", produce a section outline "
                + "with bullet points of evidence for each section. Markdown only. No final prose yet. "
                + "Prefer the provided evidence; mark gaps explicitly.",
                name: "Outline");

            var draft = new ChatClientAgent(
                chat,
                instructions:
                "You are a research writer. Produce a concise research brief from the outline: "
                + "summary, key findings (with URLs when given), open questions, and suggested next steps. Markdown. "
                + "Ground claims in the provided evidence only; if evidence is thin or missing, say so — never invent citations.",
                name: "Draft");

            Workflow pipeline = AgentWorkflowBuilder.BuildSequential(
                workflowName: "OrbitResearchPipeline",
                agents: [scope, outline, draft]);

            using var store = new FileSystemJsonCheckpointStore(new DirectoryInfo(_checkpointRoot));
            var checkpointManager = CheckpointManager.CreateJson(store, new JsonSerializerOptions());

            var userText = new StringBuilder();
            userText.Append($"Research topic: {topic.Trim()}.").Append(focusLine);
            if (hasEvidence)
            {
                userText.AppendLine();
                userText.AppendLine();
                userText.AppendLine("Evidence gathered so far (authoritative for this run — do not invent beyond it):");
                userText.AppendLine(evidence!.Trim());
            }
            else
            {
                userText.AppendLine();
                userText.AppendLine();
                userText.AppendLine(
                    "No external evidence was supplied. Scope/outline only from the topic; "
                    + "Draft must state that findings are unverified and need sources.");
            }

            var messages = new List<ChatMessage> { new(ChatRole.User, userText.ToString()) };

            var sb = new StringBuilder();
            sb.AppendLine($"# Research pipeline: {topic.Trim()}");
            if (!string.IsNullOrWhiteSpace(focus))
            {
                sb.AppendLine($"Focus: {focus.Trim()}");
            }

            sb.AppendLine();
            sb.AppendLine($"Checkpoints: `{_checkpointRoot}`");
            sb.AppendLine();

            StreamingRun run;
            if (resume)
            {
                var latest = await checkpointManager
                    .GetLatestCheckpointAsync(PipelineSessionId, ct)
                    .ConfigureAwait(false);
                if (latest is null)
                {
                    return "resume=true but no checkpoint found for this pipeline. Call again with resume=false.";
                }

                sb.AppendLine($"_Resuming from checkpoint `{latest.CheckpointId}`._");
                sb.AppendLine();
                run = await InProcessExecution
                    .ResumeStreamingAsync(pipeline, latest, checkpointManager, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                run = await InProcessExecution
                    .RunStreamingAsync(
                        pipeline,
                        messages,
                        checkpointManager,
                        sessionId: PipelineSessionId,
                        cancellationToken: ct)
                    .ConfigureAwait(false);
            }

            await using (run.ConfigureAwait(false))
            {
                await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false);

                string? lastExecutor = null;
                var completed = false;
                await foreach (WorkflowEvent evt in run.WatchStreamAsync().WithCancellation(ct)
                                   .ConfigureAwait(false))
                {
                    if (evt is AgentResponseUpdateEvent updateEvt)
                    {
                        if (!string.Equals(updateEvt.ExecutorId, lastExecutor, StringComparison.Ordinal))
                        {
                            lastExecutor = updateEvt.ExecutorId;
                            var stage = ShortExecutor(updateEvt.ExecutorId);
                            await NotifyStageAsync(stage, ct).ConfigureAwait(false);
                            sb.AppendLine();
                            sb.AppendLine($"## {stage}");
                            sb.AppendLine();
                        }

                        if (!string.IsNullOrEmpty(updateEvt.Update.Text))
                        {
                            sb.Append(updateEvt.Update.Text);
                        }
                    }
                    else if (evt is WorkflowOutputEvent)
                    {
                        completed = true;
                        sb.AppendLine();
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.AppendLine("_Pipeline completed._");
                    }
                    else if (evt is WorkflowErrorEvent errEvt)
                    {
                        sb.AppendLine();
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.AppendLine(
                            $"**Pipeline error:** {errEvt.Exception?.Message ?? "unknown error"}");
                        sb.AppendLine(
                            "_A checkpoint may exist — call run_research_pipeline with resume=true to continue._");
                    }
                }

                if (completed)
                {
                    PruneCheckpointFiles();
                }
            }

            return sb.ToString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "Research pipeline timed out after 10 minutes. A checkpoint may exist — call with resume=true.";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Cap on-disk growth: keep the newest <see cref="MaxCheckpointFiles"/> files under the root.
    /// </summary>
    internal void PruneCheckpointFiles()
    {
        try
        {
            if (!Directory.Exists(_checkpointRoot))
            {
                return;
            }

            // Only prune checkpoint payloads — never delete store index / lock sidecars.
            var files = new DirectoryInfo(_checkpointRoot)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => IsPrunableCheckpointFile(f.Name))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            if (files.Count <= MaxCheckpointFiles)
            {
                return;
            }

            foreach (var stale in files.Skip(MaxCheckpointFiles))
            {
                try
                {
                    stale.Delete();
                }
                catch
                {
                    // best-effort
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static bool IsPrunableCheckpointFile(string fileName)
    {
        // Keep index/lock/meta sidecars; prune opaque checkpoint blobs (json/bin commonly used by the store).
        if (fileName.Contains("index", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("lock", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortExecutor(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "step";
        }

        var underscore = id.IndexOf('_');
        return underscore > 0 ? id[..underscore] : id;
    }

    private async Task NotifyStageAsync(string stage, CancellationToken cancellationToken)
    {
        if (_onStage is null)
        {
            return;
        }

        try
        {
            await _onStage(stage, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Progress must not fail the pipeline.
        }
    }

}
