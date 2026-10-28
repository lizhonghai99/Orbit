using System.ClientModel;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using Orbit.Contracts;
using Serilog;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Builds an <see cref="IChatClient"/> pipeline (transport → logging → OTel → function invocation).
/// Requires a configured Responses provider API key; there is no offline Echo fallback.
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(
        string agentId,
        ILoggerFactory? loggerFactory = null,
        IReadOnlySet<string>? harness = null)
    {
        loggerFactory ??= CreateFallbackLoggerFactory();
        var mode = DescribeMode();
        if (mode == "missing_key")
        {
            throw new InvalidOperationException(
                "missing_key: configure an API key in Shell Settings (or provider env vars) before starting agents.");
        }

        IChatClient inner = CreateResponsesClient();
        // Prefer caller harness (AgentBootstrap per-participant). Fall back to this agentId's
        // manifest/env — not a different process-level ORBIT_AGENT_ID, so nested ids stay accurate.
        var enabled = harness
                      ?? HarnessProfile.ResolveEnabled(agentId: agentId);

        // Pipeline (Harness hand-assembly): logging → OTel → function invocation →
        // per-call history → optional ContextWindow compaction → AG-UI-safe args.
        var builder = inner
            .AsBuilder()
            .UseLogging(loggerFactory)
            .UseOpenTelemetry(loggerFactory, sourceName: $"Orbit.Agent.{agentId}")
            .UseFunctionInvocation(configure: ficc =>
            {
                // Precise tool spans (schema + wall clock) for Trajectory; Runtime pulls GET /telemetry/invocations.
                ficc.FunctionInvoker = static (ctx, ct) =>
                    InvocationTelemetryStore.InvokeAndRecordAsync(ctx, ct);
            })
            .UsePerServiceCallChatHistoryPersistence();

#pragma warning disable MAAI001 // Compaction APIs are experimental
        if (enabled.Contains(OrbitHarnessIds.Compaction))
        {
            var compaction = OrbitCompaction.CreateStrategy(DescribeModel());
            builder = builder.UseAIContextProviders(new CompactionProvider(compaction));
        }
#pragma warning restore MAAI001

        return builder
            .Use(static client => new AguiSafeChatClient(client))
            .Build();
    }

    public static string DescribeMode()
    {
        var (apiKey, endpoint, _) = ResolveCredentials();
        return string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(endpoint)
            ? "missing_key"
            : "responses";
    }

    public static string? DescribeModel()
    {
        var (_, _, model) = ResolveCredentials();
        return model;
    }

    private static ILoggerFactory CreateFallbackLoggerFactory() =>
        LoggerFactory.Create(b =>
        {
            // Prefer process Serilog (AgentHost / Bootstrap); console-only when tests create agents bare.
            b.AddSerilog(Log.Logger, dispose: false);
            b.SetMinimumLevel(LogLevel.Information);
        });

    private static IChatClient CreateResponsesClient()
    {
        var (apiKey, endpoint, model) = ResolveCredentials();
        model = string.IsNullOrWhiteSpace(model) ? "deepseek-flash" : model;

        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint!.TrimEnd('/') + "/") };
        var openAi = new OpenAIClient(new ApiKeyCredential(apiKey!), options);
        return openAi.GetResponsesClient().AsIChatClient(model);
    }

    private static (string? ApiKey, string? Endpoint, string? Model) ResolveCredentials()
    {
        var profile = GetEnv("ORBIT_LLM_PROFILE") ?? InferProfile();
        var model = GetEnv("ORBIT_LLM_MODEL");

        return profile?.ToLowerInvariant() switch
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

    private static string? InferProfile()
    {
        if (!string.IsNullOrWhiteSpace(GetEnv("DEEPSEEK_API_KEY")))
        {
            return "deepseek";
        }

        if (!string.IsNullOrWhiteSpace(GetEnv("MOONSHOT_API_KEY")))
        {
            return "moonshot";
        }

        if (!string.IsNullOrWhiteSpace(GetEnv("ORBIT_LLM_API_KEY")) ||
            !string.IsNullOrWhiteSpace(GetEnv("ORBIT_OPENAI_API_KEY")))
        {
            return "custom";
        }

        return null;
    }

    /// <summary>
    /// Process env wins (Supervisor injects current Shell settings there), then User, then Machine.
    /// </summary>
    private static string? GetEnv(string name) => OrbitEnv.Get(name);
}
