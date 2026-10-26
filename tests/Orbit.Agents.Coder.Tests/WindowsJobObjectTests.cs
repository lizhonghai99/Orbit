using Orbit.Agents.Coder.Coder;
using Xunit;

namespace Orbit.Agents.Coder.Tests;

public class WindowsJobObjectTests
{
    [Theory]
    [InlineData(null, 2048L * 1024 * 1024)]
    [InlineData("512", 512L * 1024 * 1024)]
    [InlineData("100", 256L * 1024 * 1024)] // floor 256MB
    public void ResolveJobMaxMemoryBytes_honors_env_and_floor(string? mb, long expectedBytes)
    {
        var prev = Environment.GetEnvironmentVariable("ORBIT_BASH_JOB_MEMORY_MB");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_JOB_MEMORY_MB", mb);
            Assert.Equal(expectedBytes, CoderShellTools.ResolveJobMaxMemoryBytes());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_JOB_MEMORY_MB", prev);
        }
    }

    [Theory]
    [InlineData(null, 32)]
    [InlineData("8", 8)]
    [InlineData("0", 32)] // invalid → default
    public void ResolveJobMaxProcesses_honors_env(string? raw, int expected)
    {
        var prev = Environment.GetEnvironmentVariable("ORBIT_BASH_JOB_MAX_PROCESSES");
        try
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_JOB_MAX_PROCESSES", raw);
            Assert.Equal(expected, CoderShellTools.ResolveJobMaxProcesses());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_BASH_JOB_MAX_PROCESSES", prev);
        }
    }

    [Fact]
    public void TryCreate_applies_process_and_memory_limits_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Job Objects are Windows-only.");
        }

        const int maxProcesses = 7;
        const long maxMemory = 64L * 1024 * 1024;
        using var job = WindowsJobObject.TryCreate(maxProcesses, maxMemory);
        Assert.NotNull(job);
        Assert.True(job!.TryGetConfiguredLimits(out var processes, out var memory));
        Assert.Equal((uint)maxProcesses, processes);
        Assert.Equal((ulong)maxMemory, memory);
    }
}
