using System.Text.Json;
using Orbit.Contracts;
using Orbit.Shell.I18n;

namespace Orbit.Shell.Services;

/// <summary>
/// Composer context meter.
/// <para><b>Occupancy</b> = last run input tokens (context window fill).</para>
/// <para><b>Lifetime</b> = sum of all provider usage in the session (billing-ish).</para>
/// </summary>
public static class ContextUsageEstimator
{
    public static ContextUsageSnapshot Estimate(
        SessionDetail? detail,
        string? modelId,
        int skillsChars = 0,
        int toolCountHint = 6)
    {
        var window = ResolveWindow(modelId, out var windowKnown);
        var lifetime = SumLifetimeUsage(detail);

        if (TryReadLastRunUsage(detail, out var lastInput, out var lastOutput, out var lastTotal))
        {
            // Occupancy ≈ what the model just consumed as input (prompt+tools in context).
            var occupancy = lastInput > 0 ? lastInput : Math.Max(0, lastTotal - lastOutput);
            var segments = new List<ContextUsageSegment>();
            if (occupancy > 0)
            {
                segments.Add(new(Loc.T("usage.seg.input"), occupancy, "#5AC8FA"));
            }

            if (lastOutput > 0)
            {
                segments.Add(new(Loc.T("usage.seg.output"), lastOutput, "#E05A3C"));
            }

            var pct = !windowKnown || window <= 0
                ? 0
                : Math.Clamp(100.0 * occupancy / window, 0, 999);

            return new ContextUsageSnapshot(
                OccupancyTokens: occupancy,
                WindowTokens: window,
                WindowKnown: windowKnown,
                PercentFull: pct,
                LifetimeTokens: lifetime.Total > 0 ? lifetime.Total : lifetime.Input + lifetime.Output,
                LifetimeInput: lifetime.Input,
                LifetimeOutput: lifetime.Output,
                Segments: segments,
                IsEstimate: false);
        }

        // Fallback: estimate occupancy from recent event payload sizes (not lifetime sum).
        var conversation = 0;
        var toolResults = 0;
        var approvals = 0;
        var thinking = 0;
        if (detail?.Entries is { Count: > 0 } entries)
        {
            // Fallback estimate: recent UI events (model history lives in .agent.json).
            var tail = entries.Count <= 80 ? entries : entries.Skip(entries.Count - 80).ToList();
            foreach (var entry in tail)
            {
                var tokens = CharsToTokens(PayloadChars(entry.Payload));
                switch (entry.Type?.ToLowerInvariant())
                {
                    case "user":
                    case "assistant":
                        conversation += tokens;
                        break;
                    case "tool_call":
                    case "tool_result":
                        toolResults += tokens;
                        break;
                    case "approval":
                        approvals += tokens;
                        break;
                    case "thinking":
                        thinking += tokens;
                        break;
                }
            }
        }

        var systemPrompt = 600;
        var toolDefs = Math.Clamp(toolCountHint, 2, 20) * 420;
        var skills = Math.Max(CharsToTokens(skillsChars), skillsChars > 0 ? 200 : 0);
        var estSegments = new List<ContextUsageSegment>
        {
            new(Loc.T("usage.seg.system"), systemPrompt, "#8E8E93"),
            new(Loc.T("usage.seg.tools"), toolDefs, "#AF52DE"),
            new(Loc.T("usage.seg.skills"), skills, "#C4872A"),
            new(Loc.T("usage.seg.tool_results"), toolResults, "#9B3A6D"),
            new(Loc.T("usage.seg.approvals"), approvals, "#D4A017"),
            new(Loc.T("usage.seg.thinking"), thinking, "#5AC8FA"),
            new(Loc.T("usage.seg.conversation"), conversation, "#E05A3C")
        }.Where(s => s.Tokens > 0).ToList();

        var occupancyEst = estSegments.Sum(s => s.Tokens);
        var pctEst = !windowKnown || window <= 0
            ? 0
            : Math.Clamp(100.0 * occupancyEst / window, 0, 999);

        return new ContextUsageSnapshot(
            OccupancyTokens: occupancyEst,
            WindowTokens: window,
            WindowKnown: windowKnown,
            PercentFull: pctEst,
            LifetimeTokens: lifetime.Total > 0 ? lifetime.Total : occupancyEst,
            LifetimeInput: lifetime.Input,
            LifetimeOutput: lifetime.Output,
            Segments: estSegments,
            IsEstimate: true);
    }

    /// <summary>
    /// Window from shared <see cref="OrbitLlmContextWindow"/>; unknown model → display 0.
    /// </summary>
    public static int ResolveWindow(string? modelId, out bool known)
    {
        // Do not inherit ORBIT_LLM_MODEL — meter follows the session/profile model only.
        var tokens = OrbitLlmContextWindow.Resolve(modelId, out known, useEnvModelFallback: false);
        return known ? tokens : 0;
    }

    public static string FormatTokens(int tokens)
    {
        if (tokens < 1000)
        {
            return tokens.ToString();
        }

        var k = tokens / 1000.0;
        return k >= 100 ? $"{k:0}K" : $"{k:0.0}K".Replace(".0K", "K", StringComparison.Ordinal);
    }

    private static bool TryReadLastRunUsage(
        SessionDetail? detail,
        out int input,
        out int output,
        out int total)
    {
        input = output = total = 0;
        if (detail?.Entries is null)
        {
            return false;
        }

        for (var i = detail.Entries.Count - 1; i >= 0; i--)
        {
            var entry = detail.Entries[i];
            if (!TryParseUsageMeta(entry, out input, out output, out total))
            {
                continue;
            }

            return input > 0 || output > 0 || total > 0;
        }

        return false;
    }

    private static (int Input, int Output, int Total) SumLifetimeUsage(SessionDetail? detail)
    {
        var input = 0;
        var output = 0;
        var total = 0;
        if (detail?.Entries is null)
        {
            return (0, 0, 0);
        }

        foreach (var entry in detail.Entries)
        {
            if (!TryParseUsageMeta(entry, out var i, out var o, out var t))
            {
                continue;
            }

            input += i;
            output += o;
            total += t;
        }

        return (input, output, total);
    }

    private static bool TryParseUsageMeta(SessionEntry entry, out int input, out int output, out int total)
    {
        input = output = total = 0;
        if (!string.Equals(entry.Type, "meta", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (entry.Payload is not JsonElement el || el.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!el.TryGetProperty("kind", out var kind) || kind.GetString() != "usage")
        {
            return false;
        }

        if (!el.TryGetProperty("source", out var src) || src.GetString() != "provider")
        {
            return false;
        }

        if (el.TryGetProperty("inputTokens", out var i) && i.TryGetInt32(out var iv))
        {
            input = iv;
        }

        if (el.TryGetProperty("outputTokens", out var o) && o.TryGetInt32(out var ov))
        {
            output = ov;
        }

        if (el.TryGetProperty("totalTokens", out var t) && t.TryGetInt32(out var tv))
        {
            total = tv;
        }

        return true;
    }

    private static int CharsToTokens(int chars) =>
        chars <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(chars / 3.2));

    private static int PayloadChars(object? payload)
    {
        if (payload is null)
        {
            return 0;
        }

        if (payload is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                return el.GetString()?.Length ?? 0;
            }

            if (el.ValueKind == JsonValueKind.Object)
            {
                var n = 0;
                foreach (var key in new[] { "content", "text", "message", "reason", "result" })
                {
                    if (el.TryGetProperty(key, out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        n += c.GetString()?.Length ?? 0;
                    }
                }

                return n == 0 ? el.GetRawText().Length : n;
            }

            return el.GetRawText().Length;
        }

        return payload.ToString()?.Length ?? 0;
    }
}

public sealed record ContextUsageSegment(string Label, int Tokens, string ColorHex);

public sealed record ContextUsageSnapshot(
    int OccupancyTokens,
    int WindowTokens,
    bool WindowKnown,
    double PercentFull,
    int LifetimeTokens,
    int LifetimeInput,
    int LifetimeOutput,
    IReadOnlyList<ContextUsageSegment> Segments,
    bool IsEstimate = true);
