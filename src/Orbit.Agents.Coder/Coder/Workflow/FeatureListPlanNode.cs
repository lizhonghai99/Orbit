using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Orbit.Agents.Hosting;

namespace Orbit.Agents.Coder.Coder.Workflow;

/// <summary>Workflow node: expand goal → Spec + FeatureList on disk.</summary>
public sealed class FeatureListPlanNode : Executor<FeatureListWorkflowState, FeatureListWorkflowState>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IChatClient _chat;

    public FeatureListPlanNode(IChatClient chat)
        : base("feature_list.plan")
    {
        _chat = chat;
    }

    public override async ValueTask<FeatureListWorkflowState> HandleAsync(
        FeatureListWorkflowState state,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (state.SkipPlan && state.Document is not null)
        {
            state.Log.AppendLine("_Resumed FeatureList from disk (plan node skipped)._");
            if (!string.IsNullOrWhiteSpace(state.Feedback))
            {
                state.Log.AppendLine("_Resume feedback will attach to newly released ready features._");
            }

            state.Log.AppendLine();
            return state;
        }

        var planner = new ChatClientAgent(
            _chat,
            instructions:
            """
            You are the plan step of a FeatureList Workflow (coding harness).
            Expand a short user goal into a product-level Spec and a structured FeatureList.
            Stay high-level on design — NOT line-level code or sprint contracts.
            Features should be dependency-aware; Generator may implement several ready items coherently.

            For each feature you MUST provide:
            - acceptance: testable product criteria (what “good” looks like)
            - verify: concrete Workspace commands that prove acceptance — matched to project kind:
              · console/CLI: "dotnet run --project Src -- --help", "dotnet test --filter Cli"
              · library: "dotnet test", "dotnet build -warnaserror"
              · API: "dotnet test", "curl -sf http://127.0.0.1:PORT/health"
              · web UI: component/unit tests; Playwright only if the product is a browser UI
              Do NOT merely restate acceptance. Do NOT invent Playwright for console/library work.

            Respond with Markdown Spec prose, then a single JSON object (fenced ```json) with shape:
            {
              "specMarkdown": "optional mirror of the Spec",
              "stack": ["..."],
              "designPrinciples": ["..."],
              "features": [
                {
                  "id": "f1",
                  "title": "...",
                  "description": "...",
                  "acceptance": ["testable criterion", "..."],
                  "verify": ["dotnet test --filter ...", "..."],
                  "dependsOn": []
                }
              ]
            }
            Do not mark statuses; the harness owns status fields.
            """,
            name: "feature_list.plan");

        var prompt = new StringBuilder();
        prompt.AppendLine($"Goal: {state.Goal}");
        if (!string.IsNullOrWhiteSpace(state.Constraints))
        {
            prompt.AppendLine($"Constraints: {state.Constraints.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(state.Feedback))
        {
            prompt.AppendLine();
            prompt.AppendLine("Prior feedback to incorporate (failures / user notes):");
            prompt.AppendLine(state.Feedback.Trim());
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var userText = attempt == 1
                    ? prompt.ToString()
                    : prompt + "\n\nPrevious reply was not valid JSON FeatureList. Return Spec + one ```json object only.";
                var session = await planner.CreateSessionAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var response = await planner.RunAsync(userText, session, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var text = response.Text ?? "";

                if (!CoderFeatureStore.TryParseJsonObject(text, out var json))
                {
                    throw new InvalidOperationException("Plan node did not return a JSON FeatureList object.");
                }

                var payload = JsonSerializer.Deserialize<CoderPlannerPayload>(json, JsonOptions)
                              ?? throw new InvalidOperationException("FeatureList JSON deserialize failed.");
                if (payload.Features is null || payload.Features.Count == 0)
                {
                    throw new InvalidOperationException("FeatureList JSON features[] is empty.");
                }

                state.Log.AppendLine("## feature_list.plan");
                state.Log.AppendLine();
                state.Log.AppendLine(text);
                state.Log.AppendLine();

                var doc = CoderFeatureStore.FromPlanner(state.Goal, state.Constraints, text, payload);
                state.Store.Save(doc);
                state.Document = doc;
                state.Feedback = null;
                return state;
            }
            catch (Exception ex) when ((ex is InvalidOperationException or JsonException) && attempt < 2)
            {
                // Retry once with a stricter prompt; attempt 2 rethrows.
                _ = ex;
            }
        }

        throw new InvalidOperationException("FeatureList plan failed after 2 attempts.");
    }
}
