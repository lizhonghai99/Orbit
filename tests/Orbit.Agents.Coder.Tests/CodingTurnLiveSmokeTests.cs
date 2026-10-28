using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Orbit.Agents.Coder.Coder;
using Orbit.Agents.Hosting;
using Orbit.Contracts;
using Xunit;

namespace Orbit.Agents.Coder.Tests;

/// <summary>
/// Tiny live coding-turn smoke: one ready feature → autonomous Generator+Evaluator.
/// Seeds FeatureList (no plan LLM). Skips without API key / Git Bash.
/// </summary>
public sealed class CodingTurnLiveSmokeTests
{
    [Fact]
    public async Task CodingTurn_writes_hello_and_evaluator_can_mark_done()
    {
        if (ChatClientFactory.DescribeMode() == "missing_key")
        {
            Assert.Skip("No LLM API key — skip coding-turn smoke.");
        }

        if (string.IsNullOrWhiteSpace(CoderShellTools.ResolveBashPath()))
        {
            Assert.Skip("Git Bash not found — skip coding-turn smoke.");
        }

        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "orbit-coder-turn-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "ws");
        var contentRoot = Path.Combine(root, "data-root");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(contentRoot);

        var prevAccess = Environment.GetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT");
        Environment.SetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT", workspace);

        try
        {
            const string agentId = "coder-turn-smoke";
            var shell = new CoderShellTools(workspace);
            var featureList = new FeatureListWorkflow(agentId, contentRoot);
            var store = CoderFeatureStore.ForAgent(agentId, contentRoot);
            store.Save(new CoderFeatureListDocument
            {
                Goal = "Write hello.txt",
                Features =
                [
                    new CoderFeatureItem
                    {
                        Id = "f1",
                        Title = "Hello file",
                        Description = "Create hello.txt with exact text Hello Orbit",
                        Acceptance = ["hello.txt exists and contains Hello Orbit"],
                        Verify = ["test -f hello.txt", "grep -F \"Hello Orbit\" hello.txt"],
                        Status = CoderFeatureStatus.Ready
                    }
                ]
            });

            var generator = BuildTurnAgent(
                agentId: "coder_generator_turn_smoke",
                displayName: "Generator (smoke)",
                instructions:
                """
                Implement only the FeatureList brief. Use file_access_write for hello.txt.
                Do NOT mark done. Summarize when ready for Evaluator.
                """,
                shell,
                featureList,
                contentRoot,
                actor: FeatureStatusActor.Generator,
                fileAccessReadOnly: false);

            var evaluator = BuildTurnAgent(
                agentId: "coder_evaluator_turn_smoke",
                displayName: "Evaluator (smoke)",
                instructions:
                """
                Grade only. Use bash verify steps. set_feature_status(done) needs VERDICT: PASS in notes,
                or failed with VERDICT: FAIL. Do not implement.
                """,
                shell,
                featureList,
                contentRoot,
                actor: FeatureStatusActor.Evaluator,
                fileAccessReadOnly: true);

            var turn = new LongRunningCodingTurn(featureList, generator, evaluator);
            // Cap wall clock — this must stay a smoke, not a marathon.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));

            var report = await turn.RunAsync(timeout.Token);
            Assert.DoesNotContain("timed out", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("No ready/in_progress", report, StringComparison.OrdinalIgnoreCase);

            var hello = Path.Combine(workspace, "hello.txt");
            Assert.True(File.Exists(hello), "Generator should create hello.txt. Report:\n" + report);
            Assert.Contains("Hello Orbit", await File.ReadAllTextAsync(hello, ct), StringComparison.Ordinal);

            var doc = store.TryLoad()!;
            var f1 = doc.Features.First(f => f.Id == "f1");
            Assert.True(
                CoderFeatureStatus.IsTerminalSuccess(f1.Status)
                || string.Equals(f1.Status, CoderFeatureStatus.Failed, StringComparison.OrdinalIgnoreCase)
                || CoderFeatureStatus.IsInProgress(f1.Status),
                $"Unexpected status={f1.Status}. Report:\n{report}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_FILE_ACCESS_ROOT", prevAccess);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static AIAgent BuildTurnAgent(
        string agentId,
        string displayName,
        string instructions,
        CoderShellTools shell,
        FeatureListWorkflow featureList,
        string contentRoot,
        FeatureStatusActor actor,
        bool fileAccessReadOnly)
    {
        var bash = AIFunctionFactory.Create(
            shell.RunBashAsync,
            name: "bash",
            description: "Run Git Bash in workspace (autonomous smoke).");

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => $"Coder workspace: {shell.WorkspaceRoot}",
                name: "get_coder_workspace"),
            bash,
            AIFunctionFactory.Create(
                () => featureList.GetFeatureListSummary(),
                name: "get_feature_list"),
            AIFunctionFactory.Create(
                (string featureId, string status, string? notes = null) =>
                    featureList.SetFeatureStatus(featureId, status, notes, actor),
                name: "set_feature_status")
        };

        var harness = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OrbitHarnessIds.FileAccess
        };
        if (!fileAccessReadOnly)
        {
            harness.Add(OrbitHarnessIds.FileMemory);
        }

        return AgentBootstrap.CreateAgent(
            agentId: agentId,
            displayName: displayName,
            instructions: instructions,
            tools: tools,
            contentRoot: contentRoot,
            harness: harness,
            includeAskUser: false,
            fileAccessReadOnly: fileAccessReadOnly,
            fileMemoryAgentId: "coder-turn-smoke");
    }
}
