using Microsoft.Extensions.AI;

namespace Orbit.Agents.Hosting.Mcp;

/// <summary>Applies a per-call timeout around an MCP (or any) <see cref="AIFunction"/>.</summary>
public sealed class TimeoutAIFunction : DelegatingAIFunction
{
    private readonly TimeSpan _timeout;

    public TimeoutAIFunction(AIFunction inner, TimeSpan timeout)
        : base(inner)
    {
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : timeout;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            return await base.InvokeCoreAsync(arguments, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP tool '{Name}' timed out after {_timeout.TotalSeconds:0}s.");
        }
    }
}
