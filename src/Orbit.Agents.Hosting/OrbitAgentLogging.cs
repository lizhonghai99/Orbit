using Orbit.Contracts;
using Serilog;
using Serilog.Events;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Agent-process Serilog bootstrap: console + rolling file under <see cref="OrbitLocalAuth.LogsDirectory"/>.
/// </summary>
public static class OrbitAgentLogging
{
    public const int RetainedFileCount = 14;

    /// <summary>Create the process-wide <see cref="Log.Logger"/> (safe to call once at process start).</summary>
    public static void Bootstrap(string agentId)
    {
        Directory.CreateDirectory(OrbitLocalAuth.LogsDirectory);
        var safeId = SanitizeFileToken(agentId);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("AgentId", agentId)
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(OrbitLocalAuth.LogsDirectory, $"agent-{safeId}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                shared: true)
            .CreateLogger();
    }

    public static string SanitizeFileToken(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return "agent";
        }

        Span<char> buffer = stackalloc char[agentId.Length];
        var n = 0;
        foreach (var ch in agentId.Trim().ToLowerInvariant())
        {
            buffer[n++] = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-';
        }

        return n == 0 ? "agent" : new string(buffer[..n]);
    }
}
