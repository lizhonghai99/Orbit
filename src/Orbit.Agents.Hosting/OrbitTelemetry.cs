using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Dev-time Activity listener so <c>UseOpenTelemetry</c> spans are visible without an OTLP collector.
/// Enable with <c>ORBIT_OTEL_CONSOLE=1</c> (Supervisor forwards into Agent processes; defaults on when Runtime did not set ASPNETCORE_ENVIRONMENT).
/// Process-wide once; listens to all <c>Orbit.Agent.*</c> / MEAI sources (not tied to the first agentId).
/// </summary>
public static class OrbitTelemetry
{
    private static int _installed;

    public static void EnsureConsoleListener(ILogger logger, string agentId)
    {
        if (!IsEnabled())
        {
            logger.LogDebug("Orbit OTel console off for {AgentId} (set ORBIT_OTEL_CONSOLE=1).", agentId);
            return;
        }

        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            logger.LogDebug("Orbit OTel console listener already installed (requested by {AgentId}).", agentId);
            return;
        }

        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name.StartsWith("Orbit.Agent", StringComparison.Ordinal)
                || source.Name.Contains("Microsoft.Extensions.AI", StringComparison.OrdinalIgnoreCase),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                try
                {
                    logger.LogInformation(
                        "otel {Source}/{Operation} {DurationMs:0}ms status={Status}",
                        activity.Source.Name,
                        activity.OperationName,
                        activity.Duration.TotalMilliseconds,
                        activity.Status);
                }
                catch
                {
                    // never break the agent on telemetry
                }
            }
        });

        logger.LogInformation("Orbit OTel console listener enabled (process-wide; agent={AgentId})", agentId);
    }

    private static bool IsEnabled()
    {
        var flag = Environment.GetEnvironmentVariable("ORBIT_OTEL_CONSOLE")
                   ?? Environment.GetEnvironmentVariable("ORBIT_OTEL_CONSOLE", EnvironmentVariableTarget.User);
        if (string.Equals(flag, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        return string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
