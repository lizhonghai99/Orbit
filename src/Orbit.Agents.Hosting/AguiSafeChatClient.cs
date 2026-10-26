using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Normalizes tool-call argument graphs before AG-UI serialization.
/// Rules live in <see cref="AguiArgumentNormalizer"/> (shared with Runtime).
/// </summary>
public sealed class AguiSafeChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(SanitizeMessages(messages), options, cancellationToken)
            .ConfigureAwait(false);
        return SanitizeResponse(response);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(SanitizeMessages(messages), options, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return SanitizeUpdate(update);
        }
    }

    private static IEnumerable<ChatMessage> SanitizeMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(SanitizeMessage);

    private static ChatMessage SanitizeMessage(ChatMessage message)
    {
        if (message.Contents is null || message.Contents.Count == 0)
        {
            return message;
        }

        var changed = false;
        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent call)
            {
                var safe = SanitizeCall(call);
                changed |= !ReferenceEquals(safe, call);
                contents.Add(safe);
            }
            else
            {
                contents.Add(content);
            }
        }

        if (!changed)
        {
            return message;
        }

        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties
        };
    }

    private static FunctionCallContent SanitizeCall(FunctionCallContent call)
    {
        if (call.Arguments is null || call.Arguments.Count == 0)
        {
            return call;
        }

        var safe = AguiArgumentNormalizer.NormalizeDictionary(call.Arguments);
        return new FunctionCallContent(call.CallId, call.Name, safe)
        {
            AdditionalProperties = call.AdditionalProperties
        };
    }

    private static ChatResponse SanitizeResponse(ChatResponse response)
    {
        var messages = response.Messages.Select(SanitizeMessage).ToList();
        return new ChatResponse(messages)
        {
            ResponseId = response.ResponseId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
            AdditionalProperties = response.AdditionalProperties
        };
    }

    private static ChatResponseUpdate SanitizeUpdate(ChatResponseUpdate update)
    {
        if (update.Contents is null || update.Contents.Count == 0)
        {
            return update;
        }

        var changed = false;
        var contents = new List<AIContent>(update.Contents.Count);
        foreach (var content in update.Contents)
        {
            if (content is FunctionCallContent call)
            {
                var safe = SanitizeCall(call);
                changed |= !ReferenceEquals(safe, call);
                contents.Add(safe);
            }
            else
            {
                contents.Add(content);
            }
        }

        if (!changed)
        {
            return update;
        }

        return new ChatResponseUpdate
        {
            AuthorName = update.AuthorName,
            Role = update.Role,
            Contents = contents,
            ResponseId = update.ResponseId,
            MessageId = update.MessageId,
            CreatedAt = update.CreatedAt,
            FinishReason = update.FinishReason,
            ModelId = update.ModelId,
            AdditionalProperties = update.AdditionalProperties
        };
    }
}
