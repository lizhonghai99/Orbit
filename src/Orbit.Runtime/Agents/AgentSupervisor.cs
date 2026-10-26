using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using Orbit.Contracts;
using Orbit.Runtime.Agui;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Llm;
using Orbit.Runtime.Sessions;
using Orbit.Runtime.Workspaces;

namespace Orbit.Runtime.Agents;

public sealed class AgentSupervisor(
    AgentRegistry registry,
    RepoPaths paths,
    ISessionStore sessions,
    AguiSessionRunner runs,
    CapabilityService capabilities,
    WorkspaceService workspaces,
    OrbitRuntimeAuth runtimeAuth,
    LlmSettingsService llmSettings,
    IHttpClientFactory httpClientFactory,
    ILogger<AgentSupervisor> logger) : IAgentSupervisor
{
    private readonly object _portGate = new();
    private readonly ConcurrentDictionary<string, Task<AgentInfo>> _startTasks = new(StringComparer.OrdinalIgnoreCase);
    private const int AgentPortMin = 5101;
    private const int AgentPortMax = 5999;
    private int _nextPort = AgentPortMin;

    private static TimeSpan DrainTimeout
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("ORBIT_STOP_DRAIN_SECONDS");
            if (int.TryParse(raw, out var seconds) && seconds >= 0)
            {
                return TimeSpan.FromSeconds(Math.Min(seconds, 300));
            }

            return TimeSpan.FromSeconds(30);
        }
    }

    /// <summary>
    /// Whether a live process looks like an Orbit agent binary under <paramref name="repoRoot"/>.
    /// Extracted for unit tests (no process kill).
    /// </summary>
    internal static bool IsOrphanAgentCandidate(string processName, string? exePath, string repoRoot)
    {
        if (!processName.StartsWith("Orbit.Agents.", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(exePath);
            var root = Path.GetFullPath(repoRoot);
            return OrbitPathNames.IsUnder(full, root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Kill leftover Orbit.Agents.* from a previous Runtime crash (same ORBIT_ROOT).</summary>
    public int CleanupOrphanAgentProcesses()
    {
        var killed = 0;
        var root = Path.GetFullPath(paths.RepoRoot);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string? exePath = null;
                try
                {
                    exePath = process.MainModule?.FileName;
                }
                catch
                {
                    // access denied / exited
                }

                if (!IsOrphanAgentCandidate(process.ProcessName, exePath, root))
                {
                    continue;
                }

                var full = Path.GetFullPath(exePath!);
                logger.LogWarning("Cleaning orphan agent process {Name} pid={Pid} path={Path}", process.ProcessName, process.Id, full);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
                killed++;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skip process during orphan cleanup");
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }

        if (killed > 0)
        {
            logger.LogInformation("Cleaned {Count} orphan agent process(es).", killed);
        }

        return killed;
    }

    public async Task StopAllAsync()
    {
        foreach (var info in registry.List())
        {
            try
            {
                if (info.Lifecycle is AgentLifecycle.Running or AgentLifecycle.Starting or AgentLifecycle.Stopping or AgentLifecycle.Faulted)
                {
                    await StopAsync(info.Id, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to stop agent {AgentId} during shutdown", info.Id);
            }
        }

        // Belt-and-suspenders: kill any orphans still holding profiles/ports.
        CleanupOrphanAgentProcesses();
    }

    public async Task<AgentInfo> StartAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(agentId, out var agent))
        {
            throw new KeyNotFoundException($"Unknown agent '{agentId}'.");
        }

        if (agent.Lifecycle == AgentLifecycle.Running)
        {
            return agent.ToInfo();
        }

        // Shared start must NOT bind the first caller's CT — a cancelled waiter must not abort
        // another caller's in-flight start. Callers cancel via WaitAsync only.
        var task = _startTasks.GetOrAdd(agentId, static (id, self) => self.StartCoreAsync(id, CancellationToken.None), this);
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
            {
                _startTasks.TryRemove(new KeyValuePair<string, Task<AgentInfo>>(agentId, task));
            }
        }
    }

    private async Task<AgentInfo> StartCoreAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(agentId, out var agent))
        {
            throw new KeyNotFoundException($"Unknown agent '{agentId}'.");
        }

        if (agent.Lifecycle == AgentLifecycle.Running)
        {
            return agent.ToInfo();
        }

        agent.LastError = null;
        agent.Lifecycle = AgentLifecycle.Starting;

        var port = AllocatePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var exe = ResolveLaunchExecutable(agent.Manifest);

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Manifest env first; Runtime-owned keys below always win.
        if (agent.Manifest.Env is { Count: > 0 } manifestEnv)
        {
            foreach (var (key, value) in manifestEnv)
            {
                if (string.IsNullOrWhiteSpace(key) || value is null)
                {
                    continue;
                }

                startInfo.Environment[key] = value;
            }
        }

        startInfo.Environment["ASPNETCORE_URLS"] = baseUrl;
        startInfo.Environment["ORBIT_AGENT_ID"] = agent.Manifest.Id;
        startInfo.Environment["ORBIT_ROOT"] = paths.RepoRoot;
        startInfo.Environment["ORBIT_PARENT_RUNTIME_PID"] = Environment.ProcessId.ToString();
        // Always set from manifest so User/Machine ORBIT_HARNESS cannot leak across agents.
        var harnessParse = OrbitHarnessIds.Parse(agent.Manifest.Harness);
        if (harnessParse.Ignored.Count > 0)
        {
            logger.LogWarning(
                "Agent {AgentId} harness ignored unknown tokens: {Tokens}",
                agentId,
                string.Join(", ", harnessParse.Ignored));
        }

        startInfo.Environment["ORBIT_HARNESS"] = string.Join(',', harnessParse.Enabled);
        if (!string.IsNullOrWhiteSpace(runtimeAuth.AgentToken))
        {
            // Agent-only secret — never the Shell token persisted to runtime.token.
            startInfo.Environment["ORBIT_RUNTIME_TOKEN"] = runtimeAuth.AgentToken;
        }

        // Agents may POST stage meta (Research) to Runtime; Agent token unlocks only that route.
        startInfo.Environment["ORBIT_RUNTIME_URL"] =
            Environment.GetEnvironmentVariable("ORBIT_RUNTIME_URL")
            ?? "http://127.0.0.1:5050";

        if (!string.IsNullOrWhiteSpace(agent.Manifest.LoginUrl))
        {
            startInfo.Environment["ORBIT_LOGIN_URL"] = agent.Manifest.LoginUrl;
        }

        var activeWorkspace = workspaces.GetActive();
        if (OrbitWorkspacePolicy.IsRequired(agent.Manifest.Workspace) && activeWorkspace is null)
        {
            agent.Lifecycle = AgentLifecycle.Stopped;
            agent.StartedWorkspacePath = null;
            throw new OrbitApiException(
                OrbitErrorCodes.WorkspaceRequired,
                $"workspace_required: agent '{agentId}' needs an active workspace.");
        }

        var injectWorkspace = OrbitWorkspacePolicy.ShouldInjectWorkspace(
            agent.Manifest.Workspace,
            activeWorkspace is not null);

        var headless = Environment.GetEnvironmentVariable("ORBIT_BROWSER_HEADLESS");
        if (!string.IsNullOrWhiteSpace(headless))
        {
            startInfo.Environment["ORBIT_BROWSER_HEADLESS"] = headless;
        }

        foreach (var key in new[]
                 {
                     "ORBIT_LLM_PROFILE",
                     "ORBIT_LLM_MODEL",
                     "ORBIT_LLM_PROTOCOL",
                     "ORBIT_LLM_API_KEY",
                     "ORBIT_LLM_ENDPOINT",
                     "ORBIT_LLM_CONTEXT_WINDOW",
                     "ORBIT_REASONING_ENABLED",
                     "ORBIT_REASONING_EFFORT",
                     // When injecting host workspace, do not let User/Machine ORBIT_*_ROOT override it.
                     "ORBIT_OPENAI_API_KEY",
                     "ORBIT_OPENAI_MODEL",
                     "ORBIT_OPENAI_ENDPOINT",
                     "ORBIT_OTEL_CONSOLE",
                     "ASPNETCORE_ENVIRONMENT",
                     "DOTNET_ENVIRONMENT",
                     "OPENAI_API_KEY",
                     "DEEPSEEK_API_KEY",
                     "DEEPSEEK_END_POINT",
                     "MOONSHOT_API_KEY",
                     "MOONSHOT_END_POINT"
                 })
        {
            // Process first (Runtime LlmSettingsService), then User/Machine legacy.
            var value = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process)
                        ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User)
                        ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(value))
            {
                startInfo.Environment[key] = value;
            }
        }

        // Runtime LLM store wins over any leftover User env for the same keys.
        foreach (var (key, value) in llmSettings.SnapshotEnvForAgents())
        {
            startInfo.Environment[key] = value;
        }

        if (!injectWorkspace)
        {
            foreach (var key in new[] { "ORBIT_CODER_ROOT", "ORBIT_CODE_ROOT", "ORBIT_FILE_ACCESS_ROOT" })
            {
                // Same order as above: Process → User → Machine.
                var value = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process)
                            ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User)
                            ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    startInfo.Environment[key] = value;
                }
            }
        }
        else
        {
            startInfo.Environment["ORBIT_WORKSPACE"] = activeWorkspace!.Path;
            startInfo.Environment["ORBIT_WORKSPACE_ID"] = activeWorkspace.Id;
            startInfo.Environment["ORBIT_CODER_ROOT"] = activeWorkspace.Path;
            startInfo.Environment["ORBIT_FILE_ACCESS_ROOT"] = activeWorkspace.Path;
        }

        // Shell-launched Runtime often has no Development; default Agent OTel console on unless explicitly 0.
        if (!startInfo.Environment.ContainsKey("ORBIT_OTEL_CONSOLE")
            && !startInfo.Environment.ContainsKey("ASPNETCORE_ENVIRONMENT"))
        {
            startInfo.Environment["ORBIT_OTEL_CONSOLE"] = "1";
        }

        logger.LogInformation("Starting agent {AgentId} at {BaseUrl} via {Exe}", agentId, baseUrl, exe);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            agent.Lifecycle = AgentLifecycle.Faulted;
            agent.LastError = ex.Message;
            throw;
        }

        agent.Process = process;
        agent.ProcessId = process.Id;
        agent.Port = port;
        agent.BaseUrl = baseUrl;

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnAgentProcessExited(agentId, process.Id);

        _ = Task.Run(() => Drain(process.StandardOutput, agentId, "stdout"), CancellationToken.None);
        _ = Task.Run(() => Drain(process.StandardError, agentId, "stderr"), CancellationToken.None);

        try
        {
            await WaitHealthyAsync(baseUrl, process, cancellationToken).ConfigureAwait(false);
            agent.Lifecycle = AgentLifecycle.Running;
            agent.StartedWorkspacePath = injectWorkspace ? activeWorkspace!.Path : null;
            logger.LogInformation("Agent {AgentId} is healthy at {BaseUrl}", agentId, baseUrl);

            try
            {
                await capabilities.PushProfileAsync(agentId, baseUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception pushEx)
            {
                logger.LogWarning(pushEx, "Failed to push capability profile to agent {AgentId}", agentId);
            }
        }
        catch (Exception ex)
        {
            agent.Lifecycle = AgentLifecycle.Faulted;
            agent.LastError = ex.Message;
            await StopProcessAsync(agent).ConfigureAwait(false);
            throw;
        }

        return agent.ToInfo();
    }

    private void OnAgentProcessExited(string agentId, int pid)
    {
        if (!registry.TryGet(agentId, out var agent))
        {
            return;
        }

        // Ignore stale handlers after we already replaced the process.
        if (agent.ProcessId != pid && agent.Process?.Id != pid)
        {
            return;
        }

        if (agent.Lifecycle is AgentLifecycle.Stopping or AgentLifecycle.Stopped)
        {
            return;
        }

        logger.LogWarning("Agent {AgentId} process exited unexpectedly (pid={Pid})", agentId, pid);
        agent.Lifecycle = AgentLifecycle.Faulted;
        agent.LastError = "Process exited unexpectedly.";
        agent.BaseUrl = null;
        agent.Port = null;
        agent.ProcessId = null;
        try
        {
            agent.Process?.Dispose();
        }
        catch
        {
            // ignore
        }

        agent.Process = null;

        try
        {
            var n = sessions.RecoverInterruptedRuns(agentId, reason: "agent_exited");
            if (n > 0)
            {
                logger.LogInformation(
                    "Marked {Count} open run(s) as interrupted after unexpected exit of {AgentId}",
                    n,
                    agentId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to recover open runs after {AgentId} exit", agentId);
        }
    }

    public async Task<AgentInfo> StopAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(agentId, out var agent))
        {
            throw new KeyNotFoundException($"Unknown agent '{agentId}'.");
        }

        if (agent.Lifecycle is AgentLifecycle.Stopped)
        {
            return agent.ToInfo();
        }

        // State machine: Stopping → refuse new runs → drain (or timeout) → reconcile JSONL → kill.
        agent.Lifecycle = AgentLifecycle.Stopping;
        logger.LogInformation("Stopping agent {AgentId}; draining in-flight runs (timeout {Timeout})…",
            agentId, DrainTimeout);

        try
        {
            var drained = await runs.DrainAgentAsync(agentId, DrainTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!drained)
            {
                logger.LogWarning(
                    "Drain timeout for {AgentId}; in-flight run(s) will be interrupted.",
                    agentId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Drain canceled for {AgentId}; proceeding to kill.", agentId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Drain failed for {AgentId}; proceeding to kill.", agentId);
        }

        var closed = sessions.RecoverInterruptedRuns(agentId, reason: "agent_stopping");
        if (closed > 0)
        {
            logger.LogInformation("Closed {Count} open run(s) for {AgentId} before kill.", closed, agentId);
        }

        await StopProcessAsync(agent).ConfigureAwait(false);
        agent.Lifecycle = AgentLifecycle.Stopped;
        agent.BaseUrl = null;
        agent.Port = null;
        agent.ProcessId = null;
        agent.LastError = null;
        agent.StartedWorkspacePath = null;
        return agent.ToInfo();
    }

    public async Task<AgentInfo> RestartAsync(string agentId, CancellationToken cancellationToken)
    {
        await StopAsync(agentId, cancellationToken).ConfigureAwait(false);
        return await StartAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReloadRunningAgentsResult> RestartRunningAsync(CancellationToken cancellationToken)
    {
        var restarted = new List<string>();
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var running = registry.List()
            .Where(a => a.Lifecycle == AgentLifecycle.Running)
            .Select(a => a.Id)
            .ToList();

        foreach (var id in running)
        {
            try
            {
                await RestartAsync(id, cancellationToken).ConfigureAwait(false);
                restarted.Add(id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to restart running agent {AgentId} after LLM change", id);
                failures[id] = ex.Message;
            }
        }

        return new ReloadRunningAgentsResult
        {
            Restarted = restarted,
            Failures = failures
        };
    }

    private async Task StopProcessAsync(AgentRegistry.TrackedAgent agent)
    {
        var process = agent.Process;
        agent.Process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop agent process {AgentId}", agent.Manifest.Id);
        }
        finally
        {
            process.Dispose();
        }
    }

    private string ResolveLaunchExecutable(AgentManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Command))
        {
            var cmd = manifest.Command.Trim();
            var resolved = Path.IsPathRooted(cmd)
                ? Path.GetFullPath(cmd)
                : Path.GetFullPath(Path.Combine(paths.RepoRoot, cmd));
            if (!File.Exists(resolved))
            {
                throw new FileNotFoundException(
                    $"Agent command not found: {manifest.Command}",
                    resolved);
            }

            return resolved;
        }

        if (string.IsNullOrWhiteSpace(manifest.Project))
        {
            throw new InvalidOperationException(
                $"Agent '{manifest.Id}' manifest requires project or command.");
        }

        return ResolveExecutable(manifest.Project);
    }

    private string ResolveExecutable(string projectRelative)
    {
        var projectPath = Path.GetFullPath(Path.Combine(paths.RepoRoot, projectRelative));
        var projectDir = Path.GetDirectoryName(projectPath)
                         ?? throw new InvalidOperationException($"Bad project path: {projectRelative}");
        var name = Path.GetFileNameWithoutExtension(projectPath);

        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(projectDir, "bin", config, "net10.0", $"{name}.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Built agent exe not found for {projectRelative}. Build the solution first (Debug net10.0).",
            projectPath);
    }

    private int AllocatePort()
    {
        lock (_portGate)
        {
            var span = AgentPortMax - AgentPortMin + 1;
            for (var n = 0; n < span; n++)
            {
                if (_nextPort < AgentPortMin || _nextPort > AgentPortMax)
                {
                    _nextPort = AgentPortMin;
                }

                var candidate = _nextPort;
                _nextPort = candidate >= AgentPortMax ? AgentPortMin : candidate + 1;
                if (IsPortFree(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"No free agent port in {AgentPortMin}-{AgentPortMax}.");
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task WaitHealthyAsync(string baseUrl, Process process, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient("agent-health");
        var deadline = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Agent process exited early (code {process.ExitCode}).");
            }

            try
            {
                using var response = await client.GetAsync($"{baseUrl}/health", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var health = await response.Content
                    .ReadFromJsonAsync<AgentProcessHealthDto>(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                // missing_key → Status=degraded / Ready=false — keep waiting / fail timeout.
                if (health is { Ready: true })
                {
                    return;
                }
            }
            catch
            {
                // not ready yet
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for {baseUrl}/health.");
    }

    private void Drain(StreamReader reader, string agentId, string stream)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                // stderr from agents is diagnostic — Information so faults are visible without Debug.
                if (string.Equals(stream, "stderr", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("[{AgentId}/{Stream}] {Line}", agentId, stream, line);
                }
                else
                {
                    logger.LogDebug("[{AgentId}/{Stream}] {Line}", agentId, stream, line);
                }
            }
        }
        catch
        {
            // process ended
        }
    }
}
