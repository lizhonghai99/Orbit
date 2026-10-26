using System.Text.Json;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>Agent-local attachment inbox under data/attachments/{agentId}/ (or AppBase).</summary>
public sealed class AttachmentStore
{
    private static readonly JsonSerializerOptions MetaJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _root;

    public AttachmentStore(string agentId)
    {
        var repoRoot = OrbitPathNames.TryGetOrbitRoot();
        _root = !string.IsNullOrWhiteSpace(repoRoot)
            ? Path.Combine(repoRoot, "data", "attachments", OrbitPathNames.SanitizeFileName(agentId))
            : Path.Combine(AppContext.BaseDirectory, "attachments");
        Directory.CreateDirectory(_root);
    }

    public async Task<AttachmentInfo> SaveAsync(
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "upload.bin";
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, safeName);
        var resolvedType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        await using (var fs = File.Create(path))
        {
            await content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(
            Path.Combine(dir, "meta.json"),
            JsonSerializer.Serialize(new { fileName = safeName, contentType = resolvedType }, MetaJson),
            cancellationToken).ConfigureAwait(false);

        var info = new FileInfo(path);
        return new AttachmentInfo
        {
            AttachmentId = id,
            FileName = safeName,
            ContentType = resolvedType,
            SizeBytes = info.Length,
            RelativePath = path,
            UploadedAt = DateTimeOffset.UtcNow
        };
    }

    public IReadOnlyList<AttachmentInfo> ListRecent(int take = 20)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(_root)
            .Select(dir =>
            {
                var file = Directory.EnumerateFiles(dir)
                    .FirstOrDefault(f => !string.Equals(Path.GetFileName(f), "meta.json", StringComparison.OrdinalIgnoreCase));
                if (file is null)
                {
                    return null;
                }

                var fi = new FileInfo(file);
                var contentType = "application/octet-stream";
                var metaPath = Path.Combine(dir, "meta.json");
                if (File.Exists(metaPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                        if (doc.RootElement.TryGetProperty("contentType", out var ct)
                            && ct.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(ct.GetString()))
                        {
                            contentType = ct.GetString()!;
                        }
                    }
                    catch
                    {
                        // keep default
                    }
                }

                return new AttachmentInfo
                {
                    AttachmentId = Path.GetFileName(dir),
                    FileName = fi.Name,
                    ContentType = contentType,
                    SizeBytes = fi.Length,
                    RelativePath = fi.FullName,
                    UploadedAt = fi.CreationTimeUtc
                };
            })
            .Where(x => x is not null)
            .Cast<AttachmentInfo>()
            .OrderByDescending(a => a.UploadedAt)
            .Take(take)
            .ToList();
    }
}
