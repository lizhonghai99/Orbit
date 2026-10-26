using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// <see cref="ContextWindowCompactionStrategy"/> defaults shared by Agent pipeline + history reducer.
/// Same knobs Shell uses for the context meter (<c>ORBIT_LLM_CONTEXT_WINDOW</c>).
/// </summary>
public static class OrbitCompaction
{
    public const int DefaultMaxContextWindowTokens = OrbitLlmContextWindow.DefaultTokens;
    public const int DefaultMaxOutputTokens = OrbitLlmContextWindow.DefaultMaxOutputTokens;

#pragma warning disable MAAI001 // Compaction APIs experimental
    public static ContextWindowCompactionStrategy CreateStrategy(string? modelId = null) =>
        new(
            maxContextWindowTokens: ResolveContextWindowTokens(modelId),
            maxOutputTokens: ResolveMaxOutputTokens());

#pragma warning disable MEAI001 // InMemoryChatHistoryProviderOptions experimental
    public static InMemoryChatHistoryProviderOptions CreateHistoryOptions(string? modelId = null)
    {
        var strategy = CreateStrategy(modelId);
        return new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = strategy.AsChatReducer(),
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.BeforeMessagesRetrieval
        };
    }
#pragma warning restore MEAI001
#pragma warning restore MAAI001

    public static int ResolveContextWindowTokens(string? modelId = null) =>
        OrbitLlmContextWindow.Resolve(modelId);

    public static int ResolveMaxOutputTokens() =>
        OrbitLlmContextWindow.ResolveMaxOutputTokens();
}
