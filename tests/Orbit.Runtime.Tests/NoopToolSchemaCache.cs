using Orbit.Runtime.Agui;

namespace Orbit.Runtime.Tests;

internal sealed class NoopToolSchemaCache : IToolSchemaCache
{
    public void Invalidate(string? agentId = null)
    {
    }
}
