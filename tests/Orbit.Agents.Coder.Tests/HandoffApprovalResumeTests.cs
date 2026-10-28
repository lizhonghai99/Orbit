using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit;

namespace Orbit.Agents.Coder.Tests;

[CollectionDefinition("HandoffApproval", DisableParallelization = true)]
public sealed class HandoffApprovalCollection;

/// <summary>
/// Framework contract (not a home-grown resume protocol):
/// <c>Workflow.AsAIAgent</c> + <c>ApprovalRequired</c> + <c>SerializeSessionAsync</c>
/// (MAF embeds the workflow checkpoint in the AgentSession) + <c>CreateResponse</c>.
/// Scripted <see cref="IChatClient"/> — no live LLM.
/// </summary>
[Collection("HandoffApproval")]
public sealed class HandoffApprovalResumeTests
{
    [Fact]
    public async Task Mid_approval_serialize_then_CreateResponse_runs_tool_and_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var toolHits = 0;
        var host = BuildHost(
            [
                AssistantCall("call-1", "run_coding_turn"),
                AssistantText("planner continues after coding turn")
            ],
            gatedTool: true,
            onTool: () => Interlocked.Increment(ref toolHits));

        var session = await host.CreateSessionAsync(cancellationToken: ct);
        var pending = await ExpectApprovalAsync(host, session, "start long task", ct);
        Assert.Equal(0, toolHits);

        // Framework path: AgentSession blob embeds workflow checkpoint; approve via CreateResponse.
        var blob = await host.SerializeSessionAsync(session, cancellationToken: ct);
        var resumed = await host.DeserializeSessionAsync(blob, cancellationToken: ct);

        var text = await CollectTextAsync(
            host.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, [pending.CreateResponse(approved: true)])],
                resumed,
                cancellationToken: ct),
            ct);

        Assert.Equal(1, toolHits);
        Assert.Contains("continues after", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_text_turn_survives_serialize_then_second_user_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = BuildHost(
            [
                AssistantText("hello from turn 1"),
                AssistantText("hello from turn 2")
            ],
            gatedTool: false);

        var session = await host.CreateSessionAsync(cancellationToken: ct);
        var turn1 = await CollectTextAsync(
            host.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "hi")],
                session,
                cancellationToken: ct),
            ct);
        Assert.Contains("turn 1", turn1, StringComparison.Ordinal);

        var blob = await host.SerializeSessionAsync(session, cancellationToken: ct);
        session = await host.DeserializeSessionAsync(blob, cancellationToken: ct);

        var turn2 = await CollectTextAsync(
            host.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "again")],
                session,
                cancellationToken: ct),
            ct);
        Assert.Contains("turn 2", turn2, StringComparison.Ordinal);
    }

    /// <summary>
    /// Documents MAF handoff + AG-UI persist risk after a coding-turn approval completed.
    /// Desired: second user turn surfaces another approval. Today often:
    /// <c>Cannot have multiple simultaneous conversations in Handoff Orchestration</c>.
    /// </summary>
    [Fact]
    public async Task After_approved_coding_turn_serialize_next_user_turn_is_fragile()
    {
        var ct = TestContext.Current.CancellationToken;
        var toolHits = 0;
        var host = BuildHost(
            [
                AssistantCall("d1", "run_coding_turn"),
                AssistantText("batch one done"),
                AssistantCall("d2", "run_coding_turn"),
                AssistantText("batch two done")
            ],
            gatedTool: true,
            onTool: () => Interlocked.Increment(ref toolHits));

        var session = await host.CreateSessionAsync(cancellationToken: ct);
        var a1 = await ExpectApprovalAsync(host, session, "turn 1", ct);
        await DrainAsync(
            host.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, [a1.CreateResponse(true)])],
                session,
                cancellationToken: ct),
            ct);
        Assert.Equal(1, toolHits);

        var blob = await host.SerializeSessionAsync(session, cancellationToken: ct);
        session = await host.DeserializeSessionAsync(blob, cancellationToken: ct);

        var (a2, trail) = await RunWithTrailAsync(host, session, "turn 2", ct);
        if (a2 is not null)
        {
            await DrainAsync(
                host.RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, [a2.CreateResponse(true)])],
                    session,
                    cancellationToken: ct),
                ct);
            Assert.Equal(2, toolHits);
            return;
        }

        Assert.Contains("simultaneous conversations", trail, StringComparison.OrdinalIgnoreCase);
    }

    private static AIAgent BuildHost(
        ChatResponse[] responses,
        bool gatedTool,
        Action? onTool = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var script = new ScriptedChatClient(responses);
        IChatClient pipeline = new ChatClientBuilder(script).UseFunctionInvocation().Build();

        List<AITool> tools = [];
        if (gatedTool)
        {
            AIFunction raw = AIFunctionFactory.Create(
                () =>
                {
                    onTool?.Invoke();
                    return "ok";
                },
                name: "run_coding_turn");
            tools.Add(new ApprovalRequiredAIFunction(raw));
        }

        AIAgent planner = new ChatClientAgent(
            pipeline,
            new ChatClientAgentOptions
            {
                Id = "coder_planner_" + suffix,
                Name = "coder_planner_" + suffix,
                ChatOptions = new ChatOptions
                {
                    Instructions = "Follow the scripted tool/text contract.",
                    Tools = tools
                }
            });
        if (gatedTool)
        {
            planner = planner.AsBuilder().UseToolApproval().Build();
        }

        return AgentWorkflowBuilder.CreateHandoffBuilderWith(planner).Build()
            .AsAIAgent(
                id: "coder_" + suffix,
                name: "coder_" + suffix,
                includeExceptionDetails: true);
    }

    private static async Task<ToolApprovalRequestContent> ExpectApprovalAsync(
        AIAgent host,
        AgentSession session,
        string userText,
        CancellationToken ct)
    {
        var (pending, trail) = await RunWithTrailAsync(host, session, userText, ct);
        Assert.True(pending is not null, "Expected ToolApprovalRequestContent. Trail: " + trail);
        return pending!;
    }

    private static async Task<(ToolApprovalRequestContent? Pending, string Trail)> RunWithTrailAsync(
        AIAgent host,
        AgentSession session,
        string userText,
        CancellationToken ct)
    {
        ToolApprovalRequestContent? pending = null;
        var trail = new List<string>();
        await foreach (var update in host.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, userText)],
                           session,
                           cancellationToken: ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                trail.Add("text:" + update.Text);
            }

            foreach (var c in update.Contents)
            {
                if (c is ErrorContent err)
                {
                    trail.Add("ErrorContent:" + (err.Message ?? err.ToString()));
                }
                else
                {
                    trail.Add(c.GetType().Name);
                }

                if (c is ToolApprovalRequestContent approval)
                {
                    pending = approval;
                }
            }
        }

        return (pending, string.Join(" | ", trail));
    }

    private static async Task DrainAsync(IAsyncEnumerable<AgentResponseUpdate> source, CancellationToken ct)
    {
        await foreach (var _ in source.WithCancellation(ct))
        {
        }
    }

    private static async Task<string> CollectTextAsync(
        IAsyncEnumerable<AgentResponseUpdate> source,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var update in source.WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
            }
        }

        return sb.ToString();
    }

    private static ChatResponse AssistantCall(string callId, string name) =>
        new([new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, name, new Dictionary<string, object?>())])]);

    private static ChatResponse AssistantText(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)]);

    private sealed class ScriptedChatClient(IEnumerable<ChatResponse> responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _queue = new(responses);

        public ChatResponse Next(IEnumerable<ChatMessage> messages)
        {
            if (_queue.Count == 0)
            {
                throw new InvalidOperationException($"Script exhausted (msgs={messages.Count()}).");
            }

            return _queue.Dequeue();
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Next(messages));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = Next(messages);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents)
                {
                    AuthorName = message.AuthorName
                };
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
