namespace Orbit.Contracts;

public sealed class AttachmentInfo
{
    public required string AttachmentId { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public long SizeBytes { get; init; }
    public required string RelativePath { get; init; }
    public DateTimeOffset UploadedAt { get; init; }
}

public sealed class UploadAttachmentResult
{
    public required AttachmentInfo Attachment { get; init; }
    public bool ForwardedToAgent { get; init; }
    public string? AgentMessage { get; init; }
}
