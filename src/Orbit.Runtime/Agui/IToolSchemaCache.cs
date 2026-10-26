namespace Orbit.Runtime.Agui;

/// <summary>Invalidates Runtime-side tool schema cache when MCP / capabilities change.</summary>
public interface IToolSchemaCache
{
    void Invalidate(string? agentId = null);
}
