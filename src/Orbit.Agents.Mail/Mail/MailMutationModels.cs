namespace Orbit.Agents.Mail.Mail;

/// <summary>Mail-local mutation DTO (not a Runtime contract). Used by Playwright helpers.</summary>
public sealed class MailMutationRequest
{
    public string? ProposalId { get; init; }
    public string? Text { get; init; }
    public string? Href { get; init; }
    public required string SuggestedAction { get; init; }
    public string? TargetFolder { get; init; }
}

public sealed class MailMutationResult
{
    public bool Ok { get; init; }
    public bool Executed { get; init; }
    public string Mode { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Url { get; init; }
    public string? Error { get; init; }
}
