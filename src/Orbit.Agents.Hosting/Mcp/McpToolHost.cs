using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Orbit.Contracts;

namespace Orbit.Agents.Hosting.Mcp;

/// <summary>
/// Loads per-agent MCP servers from <c>data/mcp/{agentId}.json</c> at process start.
/// Schema/catalog stay visible; this round does not hot-plug servers.
/// </summary>
public sealed class McpToolHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly ConcurrentDictionary<string, McpToolHost> ByAgent =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IAsyncDisposable> _clients = [];

    private McpToolHost(
        string agentId,
        bool configured,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<McpServerStatusDto> servers,
        IReadOnlyList<IAsyncDisposable> clients)
    {
        AgentId = agentId;
        Configured = configured;
        Tools = tools;
        Servers = servers;
        _clients.AddRange(clients);
    }

    public string AgentId { get; }
    public bool Configured { get; }
    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyList<McpServerStatusDto> Servers { get; }

    /// <summary>Registered MCP tool names (aliases) for this agent.</summary>
    public IReadOnlySet<string> ToolNames => CollectToolNames(Tools);

    public static McpServerStatusDto[] Snapshot(string agentId) =>
        ByAgent.TryGetValue(agentId, out var host)
            ? host.Servers.ToArray()
            : [];

    public static McpToolHost? TryGet(string agentId) =>
        ByAgent.TryGetValue(agentId, out var host) ? host : null;

    public static async Task<McpLoadResult> LoadAsync(
        string agentId,
        ILoggerFactory? loggerFactory = null,
        IEnumerable<string>? reservedToolNames = null,
        CancellationToken cancellationToken = default)
    {
        // Replace any previous host for this agent (avoid leaking stdio processes / HTTP clients).
        if (ByAgent.TryRemove(agentId, out var previous))
        {
            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }

        var path = ResolveConfigPath(agentId);
        if (!File.Exists(path))
        {
            var empty = new McpToolHost(agentId, configured: false, [], [], []);
            ByAgent[agentId] = empty;
            return new McpLoadResult(false, [], [], []);
        }

        McpConfigFile file;
        try
        {
            var raw = JsonSerializer.Deserialize<McpConfigFile>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            var (normalized, warnings) = McpConfigRules.ValidateAndNormalize(raw);
            file = normalized;
            foreach (var w in warnings)
            {
                loggerFactory?.CreateLogger("Orbit.Mcp").LogWarning("MCP config {Path}: {Warning}", path, w);
            }
        }
        catch (Exception ex)
        {
            var err = new McpServerStatusDto
            {
                Id = "_config",
                Transport = null,
                Status = "error",
                Enabled = false,
                Trusted = false,
                Error = $"invalid mcp config: {ex.Message}"
            };
            var broken = new McpToolHost(agentId, configured: true, [], [err], []);
            ByAgent[agentId] = broken;
            return new McpLoadResult(true, [], [err], []);
        }

        var servers = file.Servers ?? [];
        var statuses = new List<McpServerStatusDto>();
        var tools = new List<AITool>();
        var clients = new List<IAsyncDisposable>();
        var usedNames = new HashSet<string>(
            reservedToolNames?.Where(n => !string.IsNullOrWhiteSpace(n)) ?? [],
            StringComparer.OrdinalIgnoreCase);
        var logger = loggerFactory?.CreateLogger("Orbit.Mcp");

        foreach (var cfg in servers)
        {
            if (string.IsNullOrWhiteSpace(cfg.Id))
            {
                continue;
            }

            if (!cfg.Enabled)
            {
                statuses.Add(Status(cfg, "disabled", tools: []));
                continue;
            }

            if (!cfg.Trusted)
            {
                statuses.Add(Status(cfg, "untrusted", tools: []));
                continue;
            }

            var timeoutSec = Math.Clamp(cfg.TimeoutSeconds <= 0 ? 30 : cfg.TimeoutSeconds, 5, 300);
            using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            serverCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            try
            {
                if (!McpVariableExpansion.TryExpandEnvMap(cfg.Env, out var env, out var envErr)
                    || !McpVariableExpansion.TryExpandHeaderMap(cfg.Headers, out var headers, out envErr))
                {
                    statuses.Add(Status(cfg, "error", error: envErr, tools: []));
                    continue;
                }

                // Client owns the transport for process lifetime (dispose via McpToolHost.DisposeAsync).
                var transport = CreateTransport(cfg, env, headers);
                var client = await McpClient.CreateAsync(
                        transport,
                        clientOptions: null,
                        loggerFactory,
                        serverCts.Token)
                    .ConfigureAwait(false);
                clients.Add(client);

                var listed = await client.ListToolsAsync(cancellationToken: serverCts.Token)
                    .ConfigureAwait(false);

                var prefix = McpVariableExpansion.SanitizeToolPrefix(cfg.ToolPrefix, cfg.Id);
                var callTimeout = TimeSpan.FromSeconds(timeoutSec);
                var toolDtos = new List<McpToolDto>();
                foreach (var tool in listed)
                {
                    var alias = McpVariableExpansion.UniqueName(prefix + tool.Name, usedNames);
                    usedNames.Add(alias);
                    AITool aiTool = tool.WithName(alias);
                    // Per-call timeout (TimeoutSeconds also covers Create/List).
                    aiTool = new TimeoutAIFunction((AIFunction)aiTool, callTimeout);
                    if (ToolNeedsApproval(cfg, tool.Name))
                    {
                        aiTool = new ApprovalRequiredAIFunction((AIFunction)aiTool);
                    }

                    tools.Add(aiTool);
                    toolDtos.Add(new McpToolDto
                    {
                        Name = alias,
                        Description = tool.Description,
                        ServerId = cfg.Id
                    });
                }

                statuses.Add(Status(cfg, "connected", tools: toolDtos));
                logger?.LogInformation("MCP server {ServerId} connected with {Count} tools", cfg.Id, toolDtos.Count);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning("MCP server {ServerId} timed out after {Seconds}s", cfg.Id, timeoutSec);
                statuses.Add(Status(cfg, "error", error: $"timeout after {timeoutSec}s", tools: []));
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "MCP server {ServerId} failed", cfg.Id);
                statuses.Add(Status(cfg, "error", error: ShortError(ex), tools: []));
            }
        }

        var host = new McpToolHost(agentId, configured: true, tools, statuses, clients);
        ByAgent[agentId] = host;
        return new McpLoadResult(true, tools, statuses, clients);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // best-effort shutdown
            }
        }

        _clients.Clear();
        ByAgent.TryRemove(AgentId, out _);
    }

    /// <summary>
    /// Per-tool HITL: non-empty <see cref="McpServerConfig.ApprovalTools"/> selects by unprefixed
    /// MCP name; otherwise fall back to blanket <see cref="McpServerConfig.RequiresApproval"/>.
    /// </summary>
    public static bool ToolNeedsApproval(McpServerConfig cfg, string unprefixedToolName)
    {
        if (cfg.ApprovalTools is { Count: > 0 })
        {
            return cfg.ApprovalTools.Any(t =>
                string.Equals(t, unprefixedToolName, StringComparison.OrdinalIgnoreCase));
        }

        return cfg.RequiresApproval;
    }

    private static IClientTransport CreateTransport(
        McpServerConfig cfg,
        Dictionary<string, string?> env,
        Dictionary<string, string> headers)
    {
        var transport = (cfg.Transport ?? "stdio").Trim().ToLowerInvariant();
        return transport switch
        {
            "http" => CreateHttpTransport(cfg, headers),
            "stdio" or "" => CreateStdioTransport(cfg, env),
            // Do not silently map sse/streamablehttp → stdio (hand-edited config bypass).
            _ => throw new InvalidOperationException(
                $"Unsupported MCP transport '{cfg.Transport}'. Use stdio or http.")
        };
    }

    private static StdioClientTransport CreateStdioTransport(McpServerConfig cfg, Dictionary<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(cfg.Command))
        {
            throw new InvalidOperationException("stdio transport requires command");
        }

        var (command, args) = NormalizeStdioCommand(cfg.Command, cfg.Args);
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = cfg.Id,
            Command = command,
            Arguments = args,
            EnvironmentVariables = env,
            WorkingDirectory = Environment.GetEnvironmentVariable("ORBIT_WORKSPACE")
                               ?? OrbitPathNames.TryGetOrbitRoot()
                               ?? AppContext.BaseDirectory
        });
    }

    /// <summary>
    /// On Windows, npm/npx shims are <c>.cmd</c> — launch via <c>cmd /c</c> so CreateProcess resolves them.
    /// </summary>
    public static (string Command, IList<string> Args) NormalizeStdioCommand(
        string command,
        IList<string>? args)
    {
        var list = args?.ToList() ?? [];
        if (!OperatingSystem.IsWindows())
        {
            return (command, list);
        }

        var leaf = Path.GetFileName(command);
        if (leaf.Equals("npx", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("npm", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("node", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("pnpm", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("yarn", StringComparison.OrdinalIgnoreCase))
        {
            var forwarded = new List<string> { "/c", leaf };
            forwarded.AddRange(list);
            return ("cmd.exe", forwarded);
        }

        return (command, list);
    }

    private static HttpClientTransport CreateHttpTransport(McpServerConfig cfg, Dictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(cfg.Url))
        {
            throw new InvalidOperationException("http transport requires url");
        }

        if (!Uri.TryCreate(cfg.Url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("http transport requires https url");
        }

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = cfg.Id,
            Endpoint = uri,
            AdditionalHeaders = headers.Count == 0 ? null : headers,
            ConnectionTimeout = TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 5, 300))
        });
    }

    private static string ResolveConfigPath(string agentId)
    {
        var root = OrbitPathNames.ResolveDataRoot();
        return Path.GetFullPath(
            Path.Combine(root, "data", "mcp", $"{McpVariableExpansion.SanitizeId(agentId)}.json"));
    }

    private static McpServerStatusDto Status(
        McpServerConfig cfg,
        string status,
        string? error = null,
        List<McpToolDto>? tools = null) =>
        new()
        {
            Id = cfg.Id,
            Transport = cfg.Transport,
            Status = status,
            ToolCount = tools?.Count ?? 0,
            Trusted = cfg.Trusted,
            Enabled = cfg.Enabled,
            Error = error,
            Tools = tools ?? []
        };

    private static string ShortError(Exception ex)
    {
        var msg = McpVariableExpansion.RedactBearer(ex.Message);
        return msg.Length <= 240 ? msg : msg[..240] + "…";
    }

    public static IReadOnlySet<string> CollectToolNames(IEnumerable<AITool> tools)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (UnwrapName(tool) is { } name)
            {
                set.Add(name);
            }
        }

        return set;
    }

    private static string? UnwrapName(AITool tool)
    {
        if (tool is AIFunction direct)
        {
            return direct.Name;
        }

        foreach (var propName in new[] { "InnerFunction", "Function", "AIFunction" })
        {
            if (tool.GetType().GetProperty(propName)?.GetValue(tool) is AIFunction inner)
            {
                return inner.Name;
            }
        }

        return null;
    }
}

public sealed class McpLoadResult(
    bool configured,
    IReadOnlyList<AITool> tools,
    IReadOnlyList<McpServerStatusDto> servers,
    IReadOnlyList<IAsyncDisposable> clients)
{
    public bool Configured { get; } = configured;
    public IReadOnlyList<AITool> Tools { get; } = tools;
    public IReadOnlyList<McpServerStatusDto> Servers { get; } = servers;
    public IReadOnlyList<IAsyncDisposable> Clients { get; } = clients;

    public IReadOnlySet<string> ToolNames => McpToolHost.CollectToolNames(Tools);
}
