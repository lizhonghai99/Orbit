using Orbit.Contracts;
using Orbit.Runtime.Agents;

namespace Orbit.Runtime.Hosting;

/// <summary>
/// Graceful agent teardown on host stop — avoids sync-over-async in
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> callbacks.
/// </summary>
internal sealed class AgentProcessShutdownService(
    AgentSupervisor supervisor,
    ILogger<AgentProcessShutdownService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Runtime stopping — shutting down agent processes…");
        try
        {
            await supervisor.StopAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while stopping agents on shutdown.");
        }
        finally
        {
            OrbitLocalAuth.ReleaseTokenClaim();
        }
    }
}
