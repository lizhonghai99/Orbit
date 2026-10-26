using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Process-local catalog of model-visible tool schemas.
/// Exposed on <c>GET /tools</c> so Runtime can stamp Schema into session JSONL / capabilities.
/// </summary>
public static class AgentToolSchemaCatalog
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<ToolSchemaDto>> ByAgent =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(
        string agentId,
        IEnumerable<AITool> tools,
        IReadOnlySet<string>? mcpToolNames = null,
        IEnumerable<ToolSchemaDto>? extra = null)
    {
        var list = Describe(tools, mcpToolNames).ToList();
        if (extra is not null)
        {
            var seen = new HashSet<string>(list.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var stub in extra)
            {
                if (string.IsNullOrWhiteSpace(stub.Name) || !seen.Add(stub.Name))
                {
                    continue;
                }

                list.Add(stub);
            }
        }

        ByAgent[agentId] = list;
    }

    public static IReadOnlyList<ToolSchemaDto> Get(string agentId) =>
        ByAgent.TryGetValue(agentId, out var list) ? list : [];

    public static IReadOnlyList<ToolSchemaDto> Describe(
        IEnumerable<AITool> tools,
        IReadOnlySet<string>? mcpToolNames = null)
    {
        var list = new List<ToolSchemaDto>();
        foreach (var tool in tools)
        {
            if (UnwrapFunction(tool) is not { } fn)
            {
                continue;
            }

            JsonElement? parameters = null;
            try
            {
                var schema = fn.JsonSchema;
                if (schema.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                {
                    parameters = schema.Clone();
                }
            }
            catch
            {
                // Some wrappers omit JsonSchema.
            }

            list.Add(new ToolSchemaDto
            {
                Name = fn.Name,
                Description = fn.Description,
                Parameters = parameters,
                RequiresApproval = IsApprovalRequired(tool),
                Source = ResolveSource(fn.Name, tool, mcpToolNames)
            });
        }

        return list;
    }

    /// <summary>Domain tools only — MCP tools belong in the MCP panel, not Tools toggles.</summary>
    public static IEnumerable<ToolSchemaDto> DomainOnly(IEnumerable<ToolSchemaDto> tools) =>
        tools.Where(t => !string.Equals(t.Source, "mcp", StringComparison.OrdinalIgnoreCase));

    private static string ResolveSource(string name, AITool tool, IReadOnlySet<string>? mcpToolNames)
    {
        if (mcpToolNames is not null && mcpToolNames.Contains(name))
        {
            return "mcp";
        }

        // Fallback: MCP client tools / wrappers from ModelContextProtocol.
        for (var t = tool.GetType(); t is not null; t = t.BaseType)
        {
            if (t.Name.Contains("McpClient", StringComparison.OrdinalIgnoreCase)
                || t.FullName?.Contains("ModelContextProtocol", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "mcp";
            }
        }

        return "domain";
    }

    private static bool IsApprovalRequired(AITool tool)
    {
        for (var t = tool.GetType(); t is not null; t = t.BaseType)
        {
            if (t.Name.Contains("ApprovalRequired", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static AIFunction? UnwrapFunction(AITool tool)
    {
        if (tool is AIFunction direct)
        {
            return direct;
        }

        foreach (var name in new[] { "Function", "InnerFunction", "AIFunction", "UnderlyingFunction" })
        {
            var prop = tool.GetType().GetProperty(name);
            if (prop?.GetValue(tool) is AIFunction inner)
            {
                return inner;
            }
        }

        return null;
    }
}
