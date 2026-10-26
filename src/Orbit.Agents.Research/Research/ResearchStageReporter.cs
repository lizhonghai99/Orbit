using System.Net.Http.Json;
using System.Text.Json;
using Orbit.Agents.Hosting;
using Orbit.Contracts;

namespace Orbit.Agents.Research.Research;

/// <summary>
/// Best-effort POST of <see cref="SessionMetaKinds.ResearchStage"/> to Runtime so Shell SSE can show progress.
/// Uses agent token + <c>ORBIT_RUNTIME_URL</c> (injected by Supervisor).
/// </summary>
public static class ResearchStageReporter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task ReportAsync(string stage, CancellationToken cancellationToken)
    {
        var sessionId = OrbitRunContext.SessionId;
        var baseUrl = Environment.GetEnvironmentVariable("ORBIT_RUNTIME_URL");
        var token = Environment.GetEnvironmentVariable("ORBIT_RUNTIME_TOKEN");
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var req = SessionEvents.Meta(SessionMetaKinds.ResearchStage, new { stage });
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/sessions/{sessionId}/agent-meta")
        {
            Content = JsonContent.Create(req, options: new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };
        message.Headers.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, token);
        using var response = await Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        _ = response; // best-effort; ignore non-success
    }
}
