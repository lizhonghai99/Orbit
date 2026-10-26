using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;

namespace Orbit.Runtime.Agents;

public sealed class AgentRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly ConcurrentDictionary<string, TrackedAgent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly RepoPaths _paths;
    private readonly ILogger<AgentRegistry>? _logger;
    private volatile IReadOnlyList<AgentManifestLoadError> _loadErrors = [];

    public AgentRegistry(RepoPaths paths, ILogger<AgentRegistry>? logger = null)
    {
        _paths = paths;
        _logger = logger;
        Reload();
    }

    /// <summary>Manifest files skipped on last <see cref="Reload"/> (parse / validation failures).</summary>
    public IReadOnlyList<AgentManifestLoadError> ManifestLoadErrors => _loadErrors;

    public void Reload()
    {
        if (!Directory.Exists(_paths.AgentsDir))
        {
            _loadErrors = [];
            return;
        }

        var errors = new List<AgentManifestLoadError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_paths.AgentsDir, "*.json"))
        {
            var fileName = Path.GetFileName(file);
            try
            {
                var json = File.ReadAllText(file);
                var manifest = JsonSerializer.Deserialize<AgentManifest>(json, JsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    var msg = "missing id";
                    errors.Add(new AgentManifestLoadError { File = fileName, Error = msg });
                    _logger?.LogWarning("Skipping agent manifest {File}: {Error}", file, msg);
                    continue;
                }

                if (!OrbitPathNames.IsSafeAgentId(manifest.Id))
                {
                    var msg = "id must be ≤64 chars of [A-Za-z0-9_-] (no path segments)";
                    errors.Add(new AgentManifestLoadError { File = fileName, Error = msg });
                    _logger?.LogWarning("Skipping agent manifest {File}: {Error}", file, msg);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Project) && string.IsNullOrWhiteSpace(manifest.Command))
                {
                    var msg = "requires project or command";
                    errors.Add(new AgentManifestLoadError { File = fileName, Error = msg });
                    _logger?.LogWarning("Skipping agent manifest {File}: {Error}", file, msg);
                    continue;
                }

                seen.Add(manifest.Id);
                _agents.AddOrUpdate(
                    manifest.Id,
                    _ => new TrackedAgent(manifest),
                    (_, existing) =>
                    {
                        existing.Manifest = manifest;
                        return existing;
                    });
            }
            catch (Exception ex)
            {
                errors.Add(new AgentManifestLoadError { File = fileName, Error = ex.Message });
                // Keep previous in-memory entry if any; surface parse failures (vs silent disappear).
                _logger?.LogWarning(ex, "Skipping bad agent manifest {File}", file);
            }
        }

        _loadErrors = errors;

        foreach (var id in _agents.Keys.ToList())
        {
            if (seen.Contains(id))
            {
                continue;
            }

            // Keep running agents even if manifest file disappeared mid-session.
            if (_agents.TryGetValue(id, out var tracked)
                && tracked.Lifecycle is AgentLifecycle.Running or AgentLifecycle.Starting)
            {
                continue;
            }

            _agents.TryRemove(id, out _);
        }
    }

    public IReadOnlyList<AgentInfo> List() =>
        _agents.Values.Select(t => t.ToInfo()).OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGet(string id, out TrackedAgent agent) => _agents.TryGetValue(id, out agent!);

    public sealed class TrackedAgent(AgentManifest manifest)
    {
        public AgentManifest Manifest { get; set; } = manifest;
        public AgentLifecycle Lifecycle { get; set; } = AgentLifecycle.Stopped;
        public string? BaseUrl { get; set; }
        public int? Port { get; set; }
        public int? ProcessId { get; set; }
        public string? LastError { get; set; }
        public Process? Process { get; set; }
        /// <summary>Workspace path injected at last successful Start (null if none / policy none).</summary>
        public string? StartedWorkspacePath { get; set; }

        public AgentInfo ToInfo() => new()
        {
            Id = Manifest.Id,
            DisplayName = Manifest.DisplayName,
            Description = Manifest.Description,
            Capabilities = Manifest.Capabilities,
            Protocol = Manifest.Protocol,
            Lifecycle = Lifecycle,
            BaseUrl = BaseUrl,
            Port = Port,
            ProcessId = ProcessId,
            LastError = LastError
        };
    }
}
