using Orbit.Contracts;

namespace Orbit.Runtime.Hosting;

/// <summary>Attach Runtime→Agent <c>X-Orbit-Token</c> on named HttpClients.</summary>
public sealed class OrbitAgentAuthHandler(OrbitRuntimeAuth auth) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(auth.AgentToken))
        {
            request.Headers.Remove(OrbitLocalAuth.HeaderName);
            request.Headers.TryAddWithoutValidation(OrbitLocalAuth.HeaderName, auth.AgentToken);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
