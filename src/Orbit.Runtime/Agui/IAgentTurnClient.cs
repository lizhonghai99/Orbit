using Microsoft.Extensions.AI;
using Orbit.Contracts;
using Orbit.Runtime.Agents;

namespace Orbit.Runtime.Agui;

/// <summary>
/// One AG-UI (or future transport) turn against a running Agent. Replaceable for replay tests.
/// </summary>
public interface IAgentTurnClient
{
    Task<SessionDetail> ExecuteTurnAsync(
        string sessionId,
        AgentRegistry.TrackedAgent agent,
        IList<ChatMessage> turnMessages,
        CancellationToken cancellationToken);
}
