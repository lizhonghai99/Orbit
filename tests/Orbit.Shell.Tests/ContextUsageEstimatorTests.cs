using System.Text.Json;
using Orbit.Contracts;
using Orbit.Shell.Services;
using Xunit;

namespace Orbit.Shell.Tests;

public class ContextUsageEstimatorTests
{
    [Fact]
    public void Estimate_uses_last_run_usage_when_present()
    {
        var detail = new SessionDetail
        {
            Header = new SessionHeader
            {
                SessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                AgentId = "coder",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            Entries =
            [
                new SessionEntry
                {
                    Id = "u1",
                    Type = "meta",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        kind = "usage",
                        source = "provider",
                        inputTokens = 1200,
                        outputTokens = 80,
                        totalTokens = 1280
                    })
                }
            ]
        };

        var snap = ContextUsageEstimator.Estimate(detail, modelId: "deepseek-flash");
        Assert.False(snap.IsEstimate);
        Assert.Equal(1200, snap.OccupancyTokens);
        Assert.True(snap.WindowKnown);
        Assert.Equal(64_000, snap.WindowTokens);
        Assert.InRange(snap.PercentFull, 1.0, 5.0);
    }

    [Fact]
    public void Estimate_empty_session_is_estimate_with_baseline()
    {
        var snap = ContextUsageEstimator.Estimate(null, modelId: null);
        Assert.True(snap.IsEstimate);
        // Baseline: system (600) + toolDefs (toolCountHint 6 * 420 = 2520) = 3120 before other segments.
        Assert.True(snap.OccupancyTokens >= 3120, $"expected baseline occupancy, got {snap.OccupancyTokens}");
        Assert.False(snap.WindowKnown);
        Assert.Equal(0, snap.WindowTokens);
        Assert.Equal(0, snap.PercentFull);
    }

    [Fact]
    public void ResolveWindow_deepseek_is_64k()
    {
        var tokens = ContextUsageEstimator.ResolveWindow("deepseek-flash", out var known);
        Assert.True(known);
        Assert.Equal(64_000, tokens);
    }
}
