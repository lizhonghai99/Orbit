namespace Orbit.Contracts;

public enum AgentLifecycle
{
    Stopped,
    Starting,
    Running,
    Unhealthy,
    Stopping,
    Faulted
}

public sealed class AgentInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string Protocol { get; init; } = "ag-ui";
    public AgentLifecycle Lifecycle { get; init; }
    public string? BaseUrl { get; init; }
    public int? Port { get; init; }
    public int? ProcessId { get; init; }
    public string? LastError { get; init; }
}
