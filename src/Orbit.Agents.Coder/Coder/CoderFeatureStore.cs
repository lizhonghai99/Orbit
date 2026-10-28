using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orbit.Contracts;

namespace Orbit.Agents.Coder.Coder;

/// <summary>
/// Persists FeatureList + Spec under the <b>agent-private</b> data folder
/// (<c>data/feature-lists/{agentId}/</c>), not the host Workspace and not a shared Orbit product tree.
/// Agent-private FeatureList store for coder_planner → coder_generator → coder_evaluator.
/// </summary>
public sealed class CoderFeatureStore
{
    public const string FeatureListFileName = "feature-list.json";
    public const string SpecFileName = "spec.md";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // One gate per artefact root, shared by every CoderFeatureStore instance (OpenStore() creates new ones).
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync;

    public string Root { get; }

    public string AgentId { get; }

    /// <summary>Open an existing absolute root (tests) or resolved agent folder.</summary>
    public CoderFeatureStore(string absoluteRoot, string agentId = "coder")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteRoot);
        AgentId = string.IsNullOrWhiteSpace(agentId) ? "coder" : agentId.Trim();
        Root = Path.GetFullPath(absoluteRoot.Trim());
        _sync = PathLocks.GetOrAdd(Root, static _ => new object());
        Directory.CreateDirectory(Root);
    }

    public static CoderFeatureStore ForAgent(string agentId, string? contentRoot = null) =>
        new(ResolveRoot(agentId, contentRoot), agentId);

    /// <summary>
    /// <c>{root}/data/feature-lists/{agentId}/</c>.
    /// Explicit <paramref name="contentRoot"/> wins (tests / isolated roots); else <c>ORBIT_ROOT</c> / defaults.
    /// </summary>
    public static string ResolveRoot(string agentId, string? contentRoot = null)
    {
        var dataRoot = !string.IsNullOrWhiteSpace(contentRoot)
            ? contentRoot.Trim()
            : OrbitPathNames.ResolveDataRoot();
        return Path.GetFullPath(
            Path.Combine(dataRoot, "data", "feature-lists", OrbitPathNames.SanitizeFileName(agentId)));
    }

    public string FeatureListPath => Path.Combine(Root, FeatureListFileName);
    public string SpecPath => Path.Combine(Root, SpecFileName);

    public bool Exists => File.Exists(FeatureListPath);

    public CoderFeatureListDocument? TryLoad()
    {
        lock (_sync)
        {
            if (!File.Exists(FeatureListPath))
            {
                return null;
            }

            var json = File.ReadAllText(FeatureListPath);
            try
            {
                return JsonSerializer.Deserialize<CoderFeatureListDocument>(json, JsonOptions);
            }
            catch (JsonException)
            {
                // Crash/cancel mid-write: keep the evidence, start clean instead of failing every call.
                QuarantineCorruptFeatureList();
                return null;
            }
        }
    }

    public void Save(CoderFeatureListDocument doc)
    {
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(doc, JsonOptions);
        lock (_sync)
        {
            WriteAllTextAtomic(FeatureListPath, json);
            if (!string.IsNullOrWhiteSpace(doc.SpecMarkdown))
            {
                WriteAllTextAtomic(SpecPath, doc.SpecMarkdown.Trim() + Environment.NewLine);
            }
        }
    }

    /// <summary>
    /// Quarantine current FeatureList + Spec under lock (reset). Returns backup path prefix or null if nothing existed.
    /// </summary>
    public string? ArchiveForReset()
    {
        lock (_sync)
        {
            if (!File.Exists(FeatureListPath) && !File.Exists(SpecPath))
            {
                return null;
            }

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var prefix = FeatureListPath + $".reset-{stamp}";
            if (File.Exists(FeatureListPath))
            {
                File.Move(FeatureListPath, prefix, overwrite: true);
            }

            if (File.Exists(SpecPath))
            {
                File.Move(SpecPath, prefix + ".spec.md", overwrite: true);
            }

            return prefix;
        }
    }

    /// <summary>Temp-file + rename so a crash can never leave a half-written FeatureList.</summary>
    private static void WriteAllTextAtomic(string path, string content)
    {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, content, Encoding.UTF8);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private void QuarantineCorruptFeatureList()
    {
        try
        {
            var backup = FeatureListPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
            File.Move(FeatureListPath, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a locked file still fails closed (TryLoad returns null).
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp file cleanup is best-effort.
        }
    }

    public static string Sanitize(string id)
    {
        // OrbitPathNames also collapses ".." and separators for any on-disk id use.
        var s = OrbitPathNames.SanitizeFileName(id.Trim());
        return string.IsNullOrWhiteSpace(s) || s == "_" ? "feature" : s;
    }

    /// <summary>Extract the first JSON object from model text (raw or fenced).</summary>
    public static bool TryParseJsonObject(string text, out string json)
    {
        json = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var fence = Regex.Match(
            text,
            "```(?:json)?\\s*(\\{[\\s\\S]*?\\})\\s*```",
            RegexOptions.IgnoreCase);
        if (fence.Success)
        {
            json = fence.Groups[1].Value.Trim();
            return true;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            json = text[start..(end + 1)].Trim();
            return true;
        }

        return false;
    }

    public static CoderFeatureListDocument FromPlanner(
        string goal,
        string? constraints,
        string plannerText,
        CoderPlannerPayload payload)
    {
        var features = new List<CoderFeatureItem>();
        var i = 0;
        foreach (var f in payload.Features ?? [])
        {
            i++;
            var id = string.IsNullOrWhiteSpace(f.Id) ? $"f{i}" : f.Id.Trim();
            var verify = f.Verify?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList()
                         ?? [];
            features.Add(new CoderFeatureItem
            {
                Id = id,
                Title = string.IsNullOrWhiteSpace(f.Title) ? id : f.Title.Trim(),
                Description = f.Description?.Trim() ?? "",
                Acceptance = f.Acceptance?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList()
                             ?? [],
                Verify = verify,
                VerificationPlan = FormatVerifyPlan(verify),
                DependsOn = f.DependsOn?.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList()
                            ?? [],
                Status = CoderFeatureStatus.Pending
            });
        }

        var spec = payload.SpecMarkdown?.Trim();
        if (string.IsNullOrWhiteSpace(spec))
        {
            spec = plannerText.Trim();
        }

        return new CoderFeatureListDocument
        {
            Goal = goal.Trim(),
            Constraints = string.IsNullOrWhiteSpace(constraints) ? null : constraints.Trim(),
            SpecMarkdown = spec,
            Stack = payload.Stack?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() ?? [],
            DesignPrinciples = payload.DesignPrinciples?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList()
                               ?? [],
            Features = features
        };
    }

    public static CoderFeatureItem? PickNext(CoderFeatureListDocument doc, IReadOnlySet<string>? skip = null)
    {
        var done = new HashSet<string>(
            doc.Features.Where(f => CoderFeatureStatus.IsTerminalSuccess(f.Status)).Select(f => f.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var f in doc.Features)
        {
            // pending / failed only — never steal in_progress or re-mark ready; honor MaxAttempts.
            if (!CoderFeatureStatus.IsOpen(f.Status, f.Attempt, f.CodingTurnCount))
            {
                continue;
            }

            if (skip is not null && skip.Contains(f.Id))
            {
                continue;
            }

            if (f.DependsOn.Count > 0 && f.DependsOn.Any(d => !done.Contains(d)))
            {
                continue;
            }

            return f;
        }

        return null;
    }

    public static int CountReady(CoderFeatureListDocument doc) =>
        doc.Features.Count(f => CoderFeatureStatus.IsReady(f.Status));

    public static string? FormatVerifyPlan(IEnumerable<string>? verify)
    {
        var steps = verify?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList() ?? [];
        if (steps.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## verify");
        foreach (var step in steps)
        {
            sb.AppendLine($"- {step}");
        }

        return sb.ToString().Trim();
    }

    /// <summary>Full text for tool results / session JSONL (no Orbit-side clip; Compaction owns model context).</summary>
    public static string Truncate(string? text, int maxChars)
    {
        // Kept for callers that still want a display clip; prefer full text for durable tool returns.
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text ?? "";
        }

        return text[..maxChars] + "\n…[truncated]";
    }

    /// <summary>Reply after a single status change (full lastFindings for audit / JSONL).</summary>
    public static string SummarizeUpdate(
        CoderFeatureListDocument doc,
        CoderFeatureItem updated,
        string? artefactRoot = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"Updated `{updated.Id}` → {updated.Status} "
            + $"(attempt={updated.Attempt}, claims={updated.InProgressClaims}, "
            + $"codingTurns={updated.CodingTurnCount}/{CoderFeatureStatus.MaxCodingTurns})");
        if (!string.IsNullOrWhiteSpace(updated.LastFindings))
        {
            sb.AppendLine("lastFindings:");
            sb.AppendLine(updated.LastFindings);
        }

        sb.AppendLine(CountLine(doc));
        if (!string.IsNullOrWhiteSpace(artefactRoot))
        {
            sb.AppendLine($"Store: `{artefactRoot}`");
        }

        return sb.ToString().TrimEnd();
    }

    public static string CountLine(CoderFeatureListDocument doc)
    {
        var pending = doc.Features.Count(f =>
            string.Equals(f.Status, CoderFeatureStatus.Pending, StringComparison.OrdinalIgnoreCase));
        var ready = doc.Features.Count(f => CoderFeatureStatus.IsReady(f.Status));
        var progress = doc.Features.Count(f => CoderFeatureStatus.IsInProgress(f.Status));
        var done = doc.Features.Count(f => CoderFeatureStatus.IsTerminalSuccess(f.Status));
        var failed = doc.Features.Count(f =>
            string.Equals(f.Status, CoderFeatureStatus.Failed, StringComparison.OrdinalIgnoreCase));
        return
            $"Counts: pending={pending} ready={ready} in_progress={progress} done={done} failed={failed} total={doc.Features.Count}";
    }

    public static string Summarize(CoderFeatureListDocument doc, string? artefactRoot = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Goal: {doc.Goal}");
        sb.AppendLine(CountLine(doc));
        foreach (var f in doc.Features)
        {
            sb.AppendLine($"- [{f.Status}] {f.Id}: {f.Title}");
            if (f.Attempt > 0)
            {
                sb.AppendLine($"  attempt: {f.Attempt}");
            }

            if (f.CodingTurnCount > 0 || !CoderFeatureStatus.IsTerminalSuccess(f.Status))
            {
                var remaining = Math.Max(0, CoderFeatureStatus.MaxCodingTurns - f.CodingTurnCount);
                sb.AppendLine(
                    $"  codingTurns: {f.CodingTurnCount}/{CoderFeatureStatus.MaxCodingTurns} (remaining={remaining})");
            }

            if (f.VerifiedAt is { } at)
            {
                sb.AppendLine($"  verifiedAt: {at:O}");
            }

            if (!string.IsNullOrWhiteSpace(f.VerificationPlan))
            {
                sb.AppendLine("  verify:");
                foreach (var line in f.VerificationPlan.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (line.StartsWith("##", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    sb.AppendLine($"    {line.TrimStart('-', ' ')}");
                }
            }
            else if (f.Verify.Count > 0)
            {
                sb.AppendLine("  verify:");
                foreach (var v in f.Verify)
                {
                    sb.AppendLine($"    {v}");
                }
            }

            if (!CoderFeatureStatus.IsTerminalSuccess(f.Status) && !string.IsNullOrWhiteSpace(f.LastFindings))
            {
                sb.AppendLine("  lastFindings:");
                foreach (var line in f.LastFindings.Split('\n'))
                {
                    sb.AppendLine($"    {line}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(artefactRoot))
        {
            sb.AppendLine($"Artefacts (agent-private): `{artefactRoot}`");
            sb.AppendLine($"  - `{FeatureListFileName}`");
            sb.AppendLine($"  - `{SpecFileName}`");
        }

        return sb.ToString();
    }
}
