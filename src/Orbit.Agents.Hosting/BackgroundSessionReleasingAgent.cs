using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Ensures <see cref="BackgroundAgentsProvider.ReleaseSessionAsync"/> runs when a turn
/// completes or is cancelled — otherwise child agents keep calling LLM/MCP after Stop.
/// </summary>
#pragma warning disable MAAI001 // BackgroundAgentsProvider experimental
internal sealed class BackgroundSessionReleasingAgent : DelegatingAIAgent
{
    private readonly BackgroundAgentsProvider _background;

    public BackgroundSessionReleasingAgent(AIAgent inner, BackgroundAgentsProvider background)
        : base(inner)
    {
        _background = background;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.RunCoreAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await ReleaseQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            await ReleaseQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    private async Task ReleaseQuietlyAsync(AgentSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await _background.ReleaseSessionAsync(
                    session,
                    cancelRunning: true,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: turn teardown must not fail because a child hung.
        }
    }
}
#pragma warning restore MAAI001
