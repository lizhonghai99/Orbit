using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Orbit.Contracts;
using Orbit.Runtime.Sessions;

namespace Orbit.Runtime.Agui;

/// <summary>Load / save / one-shot seed of AgentSession next to UI JSONL.</summary>
public sealed class AgentSessionPersistence(
    ISessionStore sessions,
    AgentSessionFileStore agentSessions,
    ILogger<AgentSessionPersistence> logger)
{
#pragma warning disable MEAI001 // InMemoryChatHistoryProvider experimental
    public async Task<AgentSession> LoadOrCreateAsync(
        AIAgent remoteAgent,
        InMemoryChatHistoryProvider historyProvider,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var stored = await agentSessions.TryLoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (stored is { } element)
        {
            try
            {
                return await remoteAgent
                    .DeserializeSessionAsync(element, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Corrupt AgentSession for {SessionId}; recreating.", sessionId);
                agentSessions.Delete(sessionId);
            }
        }

        var session = await remoteAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var detail = sessions.Get(sessionId);
        if (detail is not null)
        {
            var seeded = SeedMessagesFromUiLog(detail.Entries);
            if (seeded.Count > 0)
            {
                if (seeded[^1].Role == ChatRole.User)
                {
                    seeded.RemoveAt(seeded.Count - 1);
                }

                if (seeded.Count > 0)
                {
                    historyProvider.SetMessages(session, seeded);
                    logger.LogInformation(
                        "Seeded AgentSession for {SessionId} with {Count} messages from UI log.",
                        sessionId,
                        seeded.Count);
                }
            }
        }

        return session;
    }
#pragma warning restore MEAI001

    public async Task SaveAsync(AIAgent remoteAgent, AgentSession agentSession, string sessionId, CancellationToken cancellationToken)
    {
        var serialized = await remoteAgent
            .SerializeSessionAsync(agentSession, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var agentId = sessions.Get(sessionId)?.Header.AgentId;
        await agentSessions.SaveAsync(sessionId, serialized, agentId, cancellationToken).ConfigureAwait(false);
    }

    private static List<ChatMessage> SeedMessagesFromUiLog(IReadOnlyList<SessionEntry> entries)
    {
        var messages = new List<ChatMessage>();
        foreach (var entry in entries)
        {
            switch (entry.Type?.ToLowerInvariant())
            {
                case "user":
                    {
                        var text = ExtractText(entry);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            messages.Add(new ChatMessage(ChatRole.User, text));
                        }

                        break;
                    }
                case "assistant":
                    {
                        var text = ExtractText(entry);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            messages.Add(new ChatMessage(ChatRole.Assistant, text));
                        }

                        break;
                    }
                case "tool_call":
                    {
                        if (TryReadToolCall(entry, out var callId, out var name, out var args))
                        {
                            messages.Add(new ChatMessage(
                                ChatRole.Assistant,
                                [new FunctionCallContent(callId, name, args)]));
                        }

                        break;
                    }
                case "tool_result":
                    {
                        if (TryReadToolResult(entry, out var callId, out var result))
                        {
                            messages.Add(new ChatMessage(
                                ChatRole.Tool,
                                [new FunctionResultContent(callId, result)]));
                        }

                        break;
                    }
            }
        }

        return messages.Select(AguiHistorySanitizer.SanitizeMessage).ToList();
    }

    private static bool TryReadToolCall(
        SessionEntry entry,
        out string callId,
        out string name,
        out IDictionary<string, object?>? args)
    {
        callId = "";
        name = "";
        args = null;
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        callId = payload.TryGetProperty("callId", out var id) ? id.GetString() ?? "" : "";
        name = payload.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (payload.TryGetProperty("arguments", out var a) && a.ValueKind is JsonValueKind.Object)
        {
            args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in a.EnumerateObject())
            {
                args[prop.Name] = AguiArgumentNormalizer.Normalize(prop.Value);
            }
        }

        return true;
    }

    private static bool TryReadToolResult(SessionEntry entry, out string callId, out string? result)
    {
        callId = "";
        result = null;
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        callId = payload.TryGetProperty("callId", out var id) ? id.GetString() ?? "" : "";
        result = payload.TryGetProperty("result", out var r) ? r.ToString() : null;
        return !string.IsNullOrWhiteSpace(callId);
    }

    private static string? ExtractText(SessionEntry entry)
    {
        if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "text", "content", "message" })
        {
            if (payload.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }

        return null;
    }
}
