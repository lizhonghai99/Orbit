using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Contracts;

namespace Orbit.Runtime.Llm;

/// <summary>
/// Runtime-owned LLM truth source. Persists under %LOCALAPPDATA%/Orbit (API keys DPAPI-protected).
/// Applies plaintext values to this process so AgentSupervisor can forward them to agent children.
/// </summary>
public sealed class LlmSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly ILogger _logger;
    private LlmSettingsDocument _doc;

    public LlmSettingsService()
        : this(Path.Combine(OrbitLocalAuth.TokenDirectory, "llm.settings.json"), NullLogger.Instance)
    {
    }

    public LlmSettingsService(ILogger<LlmSettingsService> logger)
        : this(Path.Combine(OrbitLocalAuth.TokenDirectory, "llm.settings.json"), logger)
    {
    }

    /// <summary>Test seam — inject a temp settings path.</summary>
    public LlmSettingsService(string settingsPath, ILogger? logger = null)
    {
        _settingsPath = settingsPath;
        _logger = logger ?? NullLogger.Instance;
        _doc = LoadOrMigrate();
        ApplyToProcessEnvironment();
    }

    public static string SettingsFilePath =>
        Path.Combine(OrbitLocalAuth.TokenDirectory, "llm.settings.json");

    public string PathInUse => _settingsPath;

    public LlmStatusDto GetStatus()
    {
        lock (_gate)
        {
            return BuildStatus(_doc);
        }
    }

    /// <summary>
    /// Explicit import of env-based keys/endpoints into the settings file.
    /// Not called on startup — Shell/UI must ask the user first.
    /// </summary>
    public LlmStatusDto ImportFromEnvironment()
    {
        lock (_gate)
        {
            var migrated = MigrateFromEnvironment();
            foreach (var (id, slot) in migrated.Profiles)
            {
                if (!_doc.Profiles.TryGetValue(id, out var existing))
                {
                    _doc.Profiles[id] = slot;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(slot.ApiKey))
                {
                    existing.ApiKey = slot.ApiKey;
                }

                if (!string.IsNullOrWhiteSpace(slot.Endpoint))
                {
                    existing.Endpoint = slot.Endpoint;
                }
            }

            if (!string.IsNullOrWhiteSpace(migrated.ActiveProfileId))
            {
                _doc.ActiveProfileId = migrated.ActiveProfileId;
            }

            if (!string.IsNullOrWhiteSpace(migrated.Model))
            {
                _doc.Model = migrated.Model;
            }

            PersistUnlocked();
            ApplyToProcessEnvironmentUnlocked();
            return BuildStatus(_doc);
        }
    }

    public LlmStatusDto SaveProfile(SaveLlmProfileRequest request)
    {
        var preset = LlmProfileCatalog.Find(request.ProfileId)
                     ?? throw new ArgumentException($"Unknown profile '{request.ProfileId}'.");

        if (string.IsNullOrWhiteSpace(request.Endpoint) ||
            !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Endpoint must be a valid HTTP/HTTPS URL.");
        }

        if (request.Protocol != LlmProtocol.Responses)
        {
            throw new NotSupportedException("This build only supports the OpenAI Responses protocol.");
        }

        var endpoint = LlmProfileCatalog.NormalizeEndpointForProtocol(request.Endpoint.Trim(), request.Protocol)!;

        lock (_gate)
        {
            var slot = _doc.Profiles.GetValueOrDefault(preset.Id) ?? new LlmProfileSlot();
            slot.Endpoint = endpoint;
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                slot.ApiKey = request.ApiKey.Trim();
            }
            else if (string.IsNullOrWhiteSpace(slot.ApiKey))
            {
                throw new InvalidOperationException(
                    "Enter an API Key (or save one first via Settings).");
            }

            _doc.Profiles[preset.Id] = slot;

            if (request.SetActive)
            {
                _doc.ActiveProfileId = preset.Id;
                _doc.Protocol = "responses";
                if (!string.IsNullOrWhiteSpace(request.Model))
                {
                    _doc.Model = request.Model.Trim();
                }
                else if (string.IsNullOrWhiteSpace(_doc.Model) && !string.IsNullOrWhiteSpace(preset.DefaultModel))
                {
                    _doc.Model = preset.DefaultModel;
                }
            }

            PersistUnlocked();
            ApplyToProcessEnvironmentUnlocked();
            return BuildStatus(_doc);
        }
    }

    public LlmStatusDto SetActiveModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model is required.", nameof(model));
        }

        lock (_gate)
        {
            _doc.Model = model.Trim();
            PersistUnlocked();
            ApplyToProcessEnvironmentUnlocked();
            return BuildStatus(_doc);
        }
    }

    public LlmStatusDto SetReasoning(bool enabled, string? effort = null)
    {
        lock (_gate)
        {
            _doc.ReasoningEnabled = enabled;
            if (enabled)
            {
                var normalized = (effort ?? _doc.ReasoningEffort ?? "medium").Trim().ToLowerInvariant();
                if (!LlmProfileCatalog.ReasoningEffortChoices.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    normalized = "medium";
                }

                _doc.ReasoningEffort = normalized;
            }

            PersistUnlocked();
            ApplyToProcessEnvironmentUnlocked();
            return BuildStatus(_doc);
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        ListLlmModelsRequest request,
        CancellationToken cancellationToken = default)
    {
        string? endpoint;
        string? apiKey;
        string? targetHost;

        lock (_gate)
        {
            var profileId = request.ProfileId ?? _doc.ActiveProfileId ?? InferActiveProfileId(_doc);
            var preset = LlmProfileCatalog.Find(profileId);
            var slot = preset is null ? null : _doc.Profiles.GetValueOrDefault(preset.Id);
            var savedEndpoint = LlmProfileCatalog.NormalizeEndpointForProtocol(
                slot?.Endpoint ?? preset?.DefaultEndpoint,
                LlmProtocol.Responses);
            var savedKey = slot?.ApiKey;

            var requestEndpoint = string.IsNullOrWhiteSpace(request.Endpoint)
                ? null
                : LlmProfileCatalog.NormalizeEndpointForProtocol(request.Endpoint.Trim(), LlmProtocol.Responses);
            var requestKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();

            var endpointFromCaller = requestEndpoint is not null;
            var keyFromCaller = requestKey is not null;

            // Forbid: caller endpoint + stored key (exfiltrates key to attacker-controlled host).
            if (endpointFromCaller && !keyFromCaller)
            {
                var matchesSaved = savedEndpoint is not null
                    && string.Equals(requestEndpoint, savedEndpoint, StringComparison.OrdinalIgnoreCase);
                if (!matchesSaved)
                {
                    throw new InvalidOperationException(
                        "Caller endpoint requires an explicit API Key; cannot use the stored key with a different endpoint.");
                }

                endpoint = savedEndpoint;
                apiKey = savedKey;
            }
            else if (endpointFromCaller && keyFromCaller)
            {
                endpoint = requestEndpoint;
                apiKey = requestKey;
            }
            else if (!endpointFromCaller && keyFromCaller)
            {
                endpoint = savedEndpoint;
                apiKey = requestKey;
            }
            else
            {
                endpoint = savedEndpoint;
                apiKey = savedKey;
            }

            targetHost = TryGetHost(endpoint);
        }

        endpoint = LlmProfileCatalog.NormalizeEndpointForProtocol(endpoint, LlmProtocol.Responses);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Endpoint and API Key are required.");
        }

        EnsureEndpointAllowed(endpoint);

        _logger.LogInformation("LLM ListModels targeting host {Host}", targetHost ?? TryGetHost(endpoint) ?? "(unknown)");

        var baseUrl = endpoint.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new HttpRequestMessage(HttpMethod.Get, CombineModelsUrl(baseUrl, "/models"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to fetch models ({(int)response.StatusCode}): {Truncate(body, 200)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Models response is missing the data array.");
        }

        return data.EnumerateArray()
            .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Snapshot of env keys agents need — Process values after <see cref="ApplyToProcessEnvironment"/>.</summary>
    public IReadOnlyDictionary<string, string> SnapshotEnvForAgents()
    {
        lock (_gate)
        {
            return BuildEnvMap(_doc);
        }
    }

    private void ApplyToProcessEnvironment()
    {
        lock (_gate)
        {
            ApplyToProcessEnvironmentUnlocked();
        }
    }

    private void ApplyToProcessEnvironmentUnlocked()
    {
        foreach (var (key, value) in BuildEnvMap(_doc))
        {
            Environment.SetEnvironmentVariable(key, value, EnvironmentVariableTarget.Process);
        }
    }

    private static Dictionary<string, string> BuildEnvMap(LlmSettingsDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var activeId = doc.ActiveProfileId ?? InferActiveProfileId(doc);
        if (!string.IsNullOrWhiteSpace(activeId))
        {
            map[LlmProfileCatalog.ActiveProfileKey] = activeId;
        }

        map[LlmProfileCatalog.ActiveProtocolKey] = doc.Protocol ?? "responses";
        if (!string.IsNullOrWhiteSpace(doc.Model))
        {
            map[LlmProfileCatalog.ActiveModelKey] = doc.Model!;
        }

        map[LlmProfileCatalog.ReasoningEnabledKey] = doc.ReasoningEnabled ? "1" : "0";
        if (!string.IsNullOrWhiteSpace(doc.ReasoningEffort))
        {
            map[LlmProfileCatalog.ReasoningEffortKey] = doc.ReasoningEffort!;
        }

        // Only the active profile's key/endpoint — do not broadcast every provider secret to every Agent.
        var active = LlmProfileCatalog.Find(activeId);
        if (active is not null && doc.Profiles.TryGetValue(active.Id, out var activeSlot))
        {
            if (!string.IsNullOrWhiteSpace(activeSlot.ApiKey))
            {
                map[active.ApiKeyEnv] = activeSlot.ApiKey!;
                map["ORBIT_LLM_API_KEY"] = activeSlot.ApiKey!;
            }

            if (!string.IsNullOrWhiteSpace(activeSlot.Endpoint))
            {
                map[active.EndpointEnv] = activeSlot.Endpoint!;
                map["ORBIT_LLM_ENDPOINT"] = activeSlot.Endpoint!;
            }
        }

        return map;
    }

    private static LlmStatusDto BuildStatus(LlmSettingsDocument doc)
    {
        var activeId = doc.ActiveProfileId ?? InferActiveProfileId(doc);
        var profiles = LlmProfileCatalog.Presets.Select(p =>
        {
            var slot = doc.Profiles.GetValueOrDefault(p.Id);
            var endpoint = LlmProfileCatalog.NormalizeEndpointForProtocol(
                slot?.Endpoint ?? p.DefaultEndpoint,
                LlmProtocol.Responses);
            var isActive = string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase);
            return new LlmProfileInfo
            {
                Id = p.Id,
                DisplayName = p.DisplayName,
                Protocol = LlmProtocol.Responses,
                Endpoint = endpoint,
                HasApiKey = !string.IsNullOrWhiteSpace(slot?.ApiKey),
                Model = isActive ? (doc.Model ?? p.DefaultModel) : p.DefaultModel,
                IsActive = isActive
            };
        }).ToList();

        var active = profiles.FirstOrDefault(p => p.IsActive);
        var mode = active is { HasApiKey: true } && !string.IsNullOrWhiteSpace(active.Endpoint)
            ? "responses"
            : "missing_key";

        string? host = null;
        if (!string.IsNullOrWhiteSpace(active?.Endpoint) &&
            Uri.TryCreate(active.Endpoint, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
        }

        return new LlmStatusDto
        {
            Mode = mode,
            ActiveProfileId = active?.Id,
            Protocol = nameof(LlmProtocol.Responses),
            Model = active?.Model,
            EndpointHost = host,
            HasApiKey = active?.HasApiKey ?? false,
            ReasoningEnabled = doc.ReasoningEnabled,
            ReasoningEffort = doc.ReasoningEffort ?? "medium",
            Profiles = profiles
        };
    }

    private LlmSettingsDocument LoadOrMigrate()
    {
        if (!File.Exists(_settingsPath))
        {
            // Empty doc — do not silently copy User/Machine env keys onto disk.
            return new LlmSettingsDocument();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<LlmSettingsDocument>(json, JsonOptions);
            if (loaded is null)
            {
                throw new InvalidDataException("LLM settings JSON deserialized to null.");
            }

            loaded.Profiles ??= new Dictionary<string, LlmProfileSlot>(StringComparer.OrdinalIgnoreCase);
            var needsRewrite = false;
            foreach (var slot in loaded.Profiles.Values)
            {
                if (string.IsNullOrWhiteSpace(slot.ApiKey))
                {
                    continue;
                }

                if (!LlmApiKeyProtector.IsProtected(slot.ApiKey))
                {
                    // Legacy plaintext on disk — keep in memory, rewrite encrypted.
                    needsRewrite = true;
                    continue;
                }

                slot.ApiKey = LlmApiKeyProtector.Unprotect(slot.ApiKey);
            }

            if (needsRewrite)
            {
                try
                {
                    Persist(loaded);
                }
                catch
                {
                    // best-effort upgrade
                }
            }

            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or CryptographicException
                                       or FormatException or InvalidOperationException)
        {
            try
            {
                var bak = _settingsPath + ".corrupt." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(_settingsPath, bak, overwrite: true);
            }
            catch
            {
                // best-effort backup
            }

            _logger.LogWarning(ex, "LLM settings corrupt at {Path}; starting empty (backup attempted)", _settingsPath);
            return new LlmSettingsDocument();
        }
    }

    private static LlmSettingsDocument MigrateFromEnvironment()
    {
        var doc = new LlmSettingsDocument();
        foreach (var preset in LlmProfileCatalog.Presets)
        {
            var key = ReadEnv(preset.ApiKeyEnv);
            var endpoint = ReadEnv(preset.EndpointEnv) ?? preset.DefaultEndpoint;
            if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(ReadEnv(preset.EndpointEnv)))
            {
                continue;
            }

            doc.Profiles[preset.Id] = new LlmProfileSlot
            {
                ApiKey = string.IsNullOrWhiteSpace(key) ? null : key,
                Endpoint = LlmProfileCatalog.NormalizeEndpointForProtocol(endpoint, LlmProtocol.Responses)
            };
        }

        doc.ActiveProfileId = ReadEnv(LlmProfileCatalog.ActiveProfileKey) ?? InferActiveProfileId(doc);
        doc.Model = ReadEnv(LlmProfileCatalog.ActiveModelKey)
                    ?? LlmProfileCatalog.Find(doc.ActiveProfileId)?.DefaultModel;
        doc.Protocol = ReadEnv(LlmProfileCatalog.ActiveProtocolKey) ?? "responses";
        var reasoning = ReadEnv(LlmProfileCatalog.ReasoningEnabledKey);
        doc.ReasoningEnabled = string.IsNullOrWhiteSpace(reasoning)
            || reasoning is "1" or "true" or "on" or "yes";
        doc.ReasoningEffort = ReadEnv(LlmProfileCatalog.ReasoningEffortKey) ?? "medium";
        return doc;
    }

    private void PersistUnlocked() => Persist(_doc);

    private void Persist(LlmSettingsDocument doc)
    {
        var dir = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Serialize a copy with protected keys so in-memory doc stays plaintext.
        var diskDoc = CloneForDisk(doc);
        var tmp = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(diskDoc, JsonOptions);
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static LlmSettingsDocument CloneForDisk(LlmSettingsDocument doc)
    {
        var clone = new LlmSettingsDocument
        {
            ActiveProfileId = doc.ActiveProfileId,
            Model = doc.Model,
            Protocol = doc.Protocol,
            ReasoningEnabled = doc.ReasoningEnabled,
            ReasoningEffort = doc.ReasoningEffort,
            Profiles = new Dictionary<string, LlmProfileSlot>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var (id, slot) in doc.Profiles)
        {
            clone.Profiles[id] = new LlmProfileSlot
            {
                Endpoint = slot.Endpoint,
                ApiKey = string.IsNullOrWhiteSpace(slot.ApiKey)
                    ? null
                    : LlmApiKeyProtector.Protect(slot.ApiKey)
            };
        }

        return clone;
    }

    private static void EnsureEndpointAllowed(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Endpoint must be an absolute URL.");
        }

        var isLoopback = uri.IsLoopback
                         || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && isLoopback)
        {
            return;
        }

        throw new InvalidOperationException("Endpoint must use HTTPS (HTTP allowed only for localhost).");
    }

    private static string? TryGetHost(string? endpoint) =>
        !string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;

    private static string InferActiveProfileId(LlmSettingsDocument doc)
    {
        foreach (var p in LlmProfileCatalog.Presets)
        {
            if (doc.Profiles.TryGetValue(p.Id, out var slot) && !string.IsNullOrWhiteSpace(slot.ApiKey))
            {
                return p.Id;
            }
        }

        return "deepseek";
    }

    private static string? ReadEnv(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    private static string CombineModelsUrl(string baseUrl, string path)
    {
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl + path["/v1".Length..];
        }

        return baseUrl + path;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private sealed class LlmSettingsDocument
    {
        public string? ActiveProfileId { get; set; }
        public string? Model { get; set; }
        public string? Protocol { get; set; } = "responses";
        public bool ReasoningEnabled { get; set; } = true;
        public string? ReasoningEffort { get; set; } = "medium";
        public Dictionary<string, LlmProfileSlot> Profiles { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LlmProfileSlot
    {
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }
    }
}
