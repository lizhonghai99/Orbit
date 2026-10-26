using Microsoft.Extensions.Logging;
using Orbit.Contracts;

namespace Orbit.Runtime.Sessions;

/// <summary>Archive + Summary generation (IO via <see cref="ISessionStore"/> + <see cref="SessionSummarizer"/>).</summary>
public sealed class SessionArchiver(ISessionStore sessions, ILogger<SessionArchiver> logger)
{
    public async Task<SessionDetail> ArchiveAsync(string sessionId, CancellationToken cancellationToken)
    {
        var detail = sessions.Get(sessionId)
                     ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        if (detail.Header.ArchivedAt is not null)
        {
            return detail;
        }

        var summary = await SessionSummarizer.SummarizeAsync(detail, logger, cancellationToken)
            .ConfigureAwait(false);
        return sessions.Archive(sessionId, summary);
    }
}
