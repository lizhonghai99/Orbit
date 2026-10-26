using System.Net.Http.Headers;
using System.Text.Json;
using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;

namespace Orbit.Runtime.Attachments;

public sealed class AttachmentService(
    RepoPaths paths,
    ISessionStore sessions,
    AgentRegistry registry,
    IHttpClientFactory httpClientFactory,
    ILogger<AttachmentService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<UploadAttachmentResult> UploadAsync(
        string sessionId,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        var detail = sessions.Get(sessionId)
                     ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        var agentId = detail.Header.AgentId;
        if (!OrbitPathNames.IsSafeAgentId(agentId))
        {
            throw new ArgumentException($"Session agent id '{agentId}' is not a safe path segment.", nameof(sessionId));
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var dir = Path.Combine(paths.DataDir, "attachments", agentId, sessionId, id);
        Directory.CreateDirectory(dir);
        // Path.GetFileName alone still allows Windows ADS names (file.txt:stream) → File.Create throws.
        var safeName = OrbitPathNames.SanitizeFileName(Path.GetFileName(fileName));
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
        {
            safeName = "upload.bin";
        }

        var fullPath = Path.Combine(dir, safeName);
        await using (var fs = File.Create(fullPath))
        {
            await content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        var fi = new FileInfo(fullPath);
        var info = new AttachmentInfo
        {
            AttachmentId = id,
            FileName = safeName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = fi.Length,
            RelativePath = Path.GetRelativePath(paths.RepoRoot, fullPath),
            UploadedAt = DateTimeOffset.UtcNow
        };

        sessions.Append(sessionId, SessionEvents.Meta(SessionMetaKinds.AttachmentUploaded, new
        {
            attachment = info
        }));

        var forwarded = false;
        string? agentMessage = null;
        if (registry.TryGet(agentId, out var agent)
            && agent.Lifecycle == AgentLifecycle.Running
            && !string.IsNullOrWhiteSpace(agent.BaseUrl))
        {
            try
            {
                using var http = httpClientFactory.CreateClient("agent-attachment");
                using var form = new MultipartFormDataContent();
                await using var read = File.OpenRead(fullPath);
                using var streamContent = new StreamContent(read);
                streamContent.Headers.ContentType = new MediaTypeHeaderValue(info.ContentType);
                form.Add(streamContent, "file", info.FileName);
                using var response = await http.PostAsync(
                    $"{agent.BaseUrl.TrimEnd('/')}/attachments",
                    form,
                    cancellationToken).ConfigureAwait(false);
                agentMessage = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                forwarded = response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Forward attachment to agent {AgentId} failed", agentId);
                agentMessage = ex.Message;
            }
        }
        else
        {
            agentMessage = "Agent is not Running; attachment saved only under Runtime data/attachments.";
        }

        return new UploadAttachmentResult
        {
            Attachment = info,
            ForwardedToAgent = forwarded,
            AgentMessage = agentMessage
        };
    }
}
