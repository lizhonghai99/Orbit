using Orbit.Contracts;
using Serilog;

namespace Orbit.Shell.Services;

/// <summary>
/// Shell process logging — same directory convention as Runtime/Agent
/// (<see cref="OrbitLocalAuth.LogsDirectory"/> / <c>shell-.log</c>).
/// </summary>
internal static class ShellFaultLog
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(OrbitLocalAuth.LogsDirectory);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.File(
                    path: Path.Combine(OrbitLocalAuth.LogsDirectory, "shell-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                .CreateLogger();
            _initialized = true;
        }
    }

    public static void Fatal(Exception ex, string message)
    {
        EnsureInitialized();
        try
        {
            Log.Fatal(ex, "{Message}", message);
            Log.CloseAndFlush();
        }
        catch
        {
            // never throw from a fault sink
        }
    }

    public static void Error(Exception ex, string message)
    {
        EnsureInitialized();
        try
        {
            Log.Error(ex, "{Message}", message);
        }
        catch
        {
        }
    }
}
