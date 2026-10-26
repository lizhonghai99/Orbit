using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Hosting;

namespace Orbit.Runtime.Sessions;

/// <summary>
/// Persists <c>AgentSession</c> JSON next to the UI JSONL log.
/// Disk format is an envelope (<c>schemaVersion</c> / <c>agentId</c> / <c>savedAt</c> / <c>session</c>).
/// </summary>
public sealed class AgentSessionFileStore(RepoPaths paths)
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string PathFor(string sessionId) =>
        Path.Combine(paths.SessionsDir, $"{SessionIdRules.RequireValid(sessionId)}.agent.json");

    public async Task<JsonElement?> TryLoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!SessionIdRules.IsValid(sessionId))
        {
            return null;
        }

        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("session", out var session)
            && root.TryGetProperty("schemaVersion", out _))
        {
            return session.Clone();
        }

        // Legacy bare AgentSession blob (pre-envelope).
        return root.Clone();
    }

    public async Task SaveAsync(
        string sessionId,
        JsonElement serialized,
        string? agentId,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.SessionsDir);
        var path = PathFor(sessionId);
        var tmp = path + ".tmp";
        var envelope = new
        {
            schemaVersion = SchemaVersion,
            agentId,
            savedAt = DateTimeOffset.UtcNow,
            session = serialized
        };
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(envelope, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string sessionId)
    {
        if (!SessionIdRules.IsValid(sessionId))
        {
            return;
        }

        var path = PathFor(sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
