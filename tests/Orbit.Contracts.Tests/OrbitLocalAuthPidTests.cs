using Orbit.Contracts;
using Xunit;

namespace Orbit.Contracts.Tests;

/// <summary>
/// <c>pid|startTicks</c> claim states against a temp directory (not %LOCALAPPDATA%).
/// </summary>
public class OrbitLocalAuthPidTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "orbit-pid-" + Guid.NewGuid().ToString("N"));

    public OrbitLocalAuthPidTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        OrbitLocalAuth.ReleaseTokenClaim(_dir);
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void Claim_same_process_is_holder()
    {
        Assert.True(OrbitLocalAuth.TryClaimRuntimePid(_dir, out var conflict), conflict);
        Assert.True(OrbitLocalAuth.IsCurrentProcessClaimHolder(_dir));
        // Re-claim while we already hold: dead/stale path not taken; CreateNew after delete of our file.
        Assert.True(OrbitLocalAuth.TryClaimRuntimePid(_dir, out conflict), conflict);
        Assert.True(OrbitLocalAuth.IsCurrentProcessClaimHolder(_dir));
    }

    [Fact]
    public void Claim_reclaims_dead_pid()
    {
        var pidPath = Path.Combine(_dir, "runtime.pid");
        // PID unlikely to exist; startTicks present so legacy "always alive" path is skipped.
        File.WriteAllText(pidPath, "2147483646|1");

        Assert.True(OrbitLocalAuth.TryClaimRuntimePid(_dir, out var conflict), conflict);
        Assert.True(OrbitLocalAuth.IsCurrentProcessClaimHolder(_dir));
    }

    [Fact]
    public void Claim_reclaims_recycled_pid_with_mismatched_startTicks()
    {
        var pidPath = Path.Combine(_dir, "runtime.pid");
        // Current PID is live, but startTicks=1 will not match → treated as recycled → reclaim.
        File.WriteAllText(pidPath, $"{Environment.ProcessId}|1");

        Assert.True(OrbitLocalAuth.TryClaimRuntimePid(_dir, out var conflict), conflict);
        Assert.True(OrbitLocalAuth.IsCurrentProcessClaimHolder(_dir));
        var raw = File.ReadAllText(pidPath).Trim();
        Assert.StartsWith($"{Environment.ProcessId}|", raw, StringComparison.Ordinal);
        var parts = raw.Split('|');
        Assert.Equal(2, parts.Length);
        Assert.NotEqual("1", parts[1]);
    }
}
