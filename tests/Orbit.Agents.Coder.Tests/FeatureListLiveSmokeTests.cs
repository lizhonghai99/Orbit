using Orbit.Agents.Coder.Coder;
using Orbit.Agents.Hosting;
using Xunit;

namespace Orbit.Agents.Coder.Tests;

/// <summary>
/// Tiny live LLM smoke (not a long coding marathon): plan → release one ready feature.
/// Skips when no Responses API key is configured.
/// </summary>
public sealed class FeatureListLiveSmokeTests
{
    [Fact]
    public async Task RunAsync_tiny_goal_plans_and_releases_one_ready()
    {
        if (ChatClientFactory.DescribeMode() == "missing_key")
        {
            Assert.Skip("No LLM API key in env — skip live FeatureList smoke.");
        }

        var ct = TestContext.Current.CancellationToken;
        var contentRoot = Path.Combine(
            Path.GetTempPath(),
            "orbit-coder-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        try
        {
            var agentId = "coder-live-smoke";
            var workflow = new FeatureListWorkflow(
                agentId,
                contentRoot,
                chatFactory: () => ChatClientFactory.Create("coder-feature-list", harness: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                loggerFactory: null);

            // Keep it tiny: one feature, console hello — no multi-batch coding marathon.
            var report = await workflow.RunAsync(
                goal: "Create a one-file C# console that prints Hello Orbit.",
                constraints: "Single feature only. Prefer `dotnet new console`. No web/UI.",
                feedback: null,
                maxFeaturesPerRun: 1,
                resume: false,
                reset: false,
                cancellationToken: ct);

            Assert.DoesNotContain("timed out", report, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ready", report, StringComparison.OrdinalIgnoreCase);

            var store = CoderFeatureStore.ForAgent(agentId, contentRoot);
            var doc = store.TryLoad();
            Assert.NotNull(doc);
            Assert.True(doc!.Features.Count >= 1);
            Assert.Contains(
                doc.Features,
                f => CoderFeatureStatus.IsReady(f.Status));

            var claim = workflow.ClaimReadyForCodingTurn();
            Assert.True(claim.Ok, claim.Message);
            Assert.NotEmpty(claim.ClaimedIds);
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }
}
