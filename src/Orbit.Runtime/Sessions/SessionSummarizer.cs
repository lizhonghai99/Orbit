using System.ClientModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using Orbit.Contracts;

namespace Orbit.Runtime.Sessions;

/// <summary>
/// One-shot archive Summary via the same Responses credentials Agents use.
/// Falls back to an extractive stub when the API key is missing or the call fails/times out.
/// </summary>
public static class SessionSummarizer
{
    private static readonly TimeSpan LlmTimeout = TimeSpan.FromSeconds(30);

    public static async Task<string> SummarizeAsync(
        SessionDetail detail,
        CancellationToken cancellationToken) =>
        await SummarizeAsync(detail, logger: null, cancellationToken).ConfigureAwait(false);

    public static async Task<string> SummarizeAsync(
        SessionDetail detail,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var transcript = BuildTranscript(detail);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return "(empty session)";
        }

        if (!TryCreateClient(out var client, out var model))
        {
            logger?.LogDebug("Archive summary: no LLM credentials; using extractive fallback");
            return ExtractiveFallback(transcript);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(LlmTimeout);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "Summarize this Agent chat for an archive index. "
                    + "Reply in the same language as the user messages. "
                    + "3–5 short bullets or one short paragraph. No tools. No preamble."),
                new(ChatRole.User, transcript)
            };
            var options = new ChatOptions
            {
                ModelId = model,
                MaxOutputTokens = 400,
                Temperature = 0.2f
            };

            logger?.LogInformation("Archive summary via model {Model} (timeout {Timeout}s)",
                model, LlmTimeout.TotalSeconds);

            var response = await client.GetResponseAsync(messages, options, timeoutCts.Token)
                .ConfigureAwait(false);
            var text = response.Text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? ExtractiveFallback(transcript) : text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Archive summary timed out after {Timeout}s; using extractive fallback",
                LlmTimeout.TotalSeconds);
            return ExtractiveFallback(transcript);
        }
        catch (Exception ex)
        {
            // Do not leak exception text into user-visible archive Summary.
            logger?.LogWarning(ex, "Archive summary LLM failed; using extractive fallback");
            return ExtractiveFallback(transcript);
        }
    }

    private static string BuildTranscript(SessionDetail detail)
    {
        var sb = new StringBuilder();
        foreach (var entry in detail.Entries)
        {
            if (entry.Payload is not JsonElement payload || payload.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? text = null;
            if (payload.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                text = t.GetString();
            }
            else if (payload.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                text = m.GetString();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var role = entry.Type.ToLowerInvariant() switch
            {
                SessionEventTypes.User => "User",
                SessionEventTypes.Assistant => "Assistant",
                SessionEventTypes.Thinking => "Thinking",
                _ => null
            };
            if (role is null)
            {
                continue;
            }

            if (text.Length > 1200)
            {
                text = text[..1200] + "…";
            }

            sb.AppendLine($"{role}: {text}");
            if (sb.Length > 12_000)
            {
                sb.AppendLine("…(truncated)");
                break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string ExtractiveFallback(string transcript)
    {
        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var take = Math.Min(6, lines.Length);
        return string.Join('\n', lines.Take(take));
    }

    private static bool TryCreateClient(out IChatClient client, out string? model)
    {
        client = null!;
        model = null;
        var (apiKey, endpoint, resolvedModel) = ResolveCredentials();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        model = string.IsNullOrWhiteSpace(resolvedModel) ? "deepseek-flash" : resolvedModel;
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint.TrimEnd('/') + "/") };
        var openAi = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        client = openAi.GetResponsesClient().AsIChatClient(model);
        return true;
    }

    private static (string? ApiKey, string? Endpoint, string? Model) ResolveCredentials()
    {
        var profile = GetEnv("ORBIT_LLM_PROFILE");
        var model = GetEnv("ORBIT_LLM_MODEL");
        return (profile ?? "").ToLowerInvariant() switch
        {
            "deepseek" => (
                GetEnv("DEEPSEEK_API_KEY"),
                LlmProfileCatalog.NormalizeEndpointForProtocol(
                    GetEnv("DEEPSEEK_END_POINT") ?? "https://api.deepseek.com", LlmProtocol.Responses),
                model ?? "deepseek-flash"),
            "moonshot" => (
                GetEnv("MOONSHOT_API_KEY"),
                LlmProfileCatalog.NormalizeEndpointForProtocol(
                    GetEnv("MOONSHOT_END_POINT") ?? "https://api.moonshot.cn/v1", LlmProtocol.Responses),
                model ?? "kimi-k2.6"),
            "custom" => (
                GetEnv("ORBIT_LLM_API_KEY") ?? GetEnv("ORBIT_OPENAI_API_KEY") ?? GetEnv("OPENAI_API_KEY"),
                LlmProfileCatalog.NormalizeEndpointForProtocol(
                    GetEnv("ORBIT_LLM_ENDPOINT") ?? GetEnv("ORBIT_OPENAI_ENDPOINT"), LlmProtocol.Responses),
                model ?? GetEnv("ORBIT_OPENAI_MODEL")),
            _ => (
                GetEnv("ORBIT_LLM_API_KEY")
                ?? GetEnv("DEEPSEEK_API_KEY")
                ?? GetEnv("MOONSHOT_API_KEY")
                ?? GetEnv("ORBIT_OPENAI_API_KEY")
                ?? GetEnv("OPENAI_API_KEY"),
                LlmProfileCatalog.NormalizeEndpointForProtocol(
                    GetEnv("ORBIT_LLM_ENDPOINT")
                    ?? GetEnv("DEEPSEEK_END_POINT")
                    ?? GetEnv("MOONSHOT_END_POINT")
                    ?? GetEnv("ORBIT_OPENAI_ENDPOINT"),
                    LlmProtocol.Responses),
                model ?? GetEnv("ORBIT_OPENAI_MODEL"))
        };
    }

    private static string? GetEnv(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
}
