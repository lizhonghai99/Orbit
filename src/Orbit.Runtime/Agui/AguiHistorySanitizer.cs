using Microsoft.Extensions.AI;
using Orbit.Contracts;

namespace Orbit.Runtime.Agui;

/// <summary>
/// Keeps outbound AG-UI history tool args serializable.
/// Normalization rules: <see cref="AguiArgumentNormalizer"/> (same as Agent <c>AguiSafeChatClient</c>).
/// </summary>
internal static class AguiHistorySanitizer
{
    public static ChatMessage SanitizeMessage(ChatMessage message)
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
                var safeArgs = AguiArgumentNormalizer.NormalizeDictionary(call.Arguments);
                if (safeArgs is null || ReferenceEquals(safeArgs, call.Arguments))
                {
                    contents.Add(call);
                }
                else
                {
                    changed = true;
                    contents.Add(new FunctionCallContent(call.CallId, call.Name, safeArgs)
                    {
                        AdditionalProperties = call.AdditionalProperties
                    });
                }
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

    public static IDictionary<string, object?>? ToStorageArgs(IDictionary<string, object?>? args) =>
        AguiArgumentNormalizer.NormalizeDictionary(args);
}
