using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Attachments;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Sessions;
using Xunit;

namespace Orbit.Runtime.Tests;

public class AttachmentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "orbit-att-" + Guid.NewGuid().ToString("N"));
    private readonly JsonlSessionStore _store;
    private readonly AttachmentService _attachments;

    public AttachmentServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "sessions"));
        var paths = new RepoPaths { RepoRoot = _root };
        _store = new JsonlSessionStore(paths);
        _attachments = new AttachmentService(
            paths,
            _store,
            new AgentRegistry(paths),
            new NoopHttpFactory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AttachmentService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public async Task Upload_sanitizes_ads_filename()
    {
        var sid = _store.Create(new CreateSessionRequest { AgentId = "coder", Title = "t" }).Header.SessionId;
        await using var content = new MemoryStream("hi"u8.ToArray());
        var result = await _attachments.UploadAsync(
            sid,
            @"evil.txt:Zone.Identifier",
            "text/plain",
            content,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.DoesNotContain(':', result.Attachment.FileName);
        Assert.True(File.Exists(Path.Combine(_root, result.Attachment.RelativePath)));
    }

    private sealed class NoopHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
