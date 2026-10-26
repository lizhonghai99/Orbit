using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Orbit.Contracts;
using Orbit.Runtime.Agents;
using Orbit.Runtime.Agui;
using Orbit.Runtime.Approvals;
using Orbit.Runtime.Attachments;
using Orbit.Runtime.Hosting;
using Orbit.Runtime.Llm;
using Orbit.Runtime.Sessions;
using Orbit.Runtime.Workspaces;
using Serilog;

Directory.CreateDirectory(OrbitLocalAuth.LogsDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(OrbitLocalAuth.LogsDirectory, "runtime-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5050");
    // Multi-agent StopAll can exceed the default 30s host shutdown budget.
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMinutes(2));

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

    builder.Services.AddSingleton(RepoPaths.Resolve());
    builder.Services.AddSingleton<OrbitRuntimeAuth>();
    builder.Services.AddSingleton<LlmSettingsService>();
    builder.Services.AddSingleton<AgentRegistry>();
    builder.Services.AddSingleton<WorkspaceService>();
    builder.Services.AddSingleton<WorkspaceAgentRestarter>();
    builder.Services.AddSingleton<AgentSupervisor>();
    builder.Services.AddSingleton<IAgentSupervisor>(sp => sp.GetRequiredService<AgentSupervisor>());
    builder.Services.AddSingleton<ISessionStore, JsonlSessionStore>();
    builder.Services.AddSingleton<AgentSessionFileStore>();
    builder.Services.AddSingleton<TurnTelemetry>();
    builder.Services.AddSingleton<TurnEventProjector>();
    builder.Services.AddSingleton<AgentSessionPersistence>();
    builder.Services.AddSingleton<SessionArchiver>();
    builder.Services.AddSingleton<AguiSessionRunner>();
    builder.Services.AddSingleton<IToolSchemaCache>(sp => sp.GetRequiredService<AguiSessionRunner>());
    builder.Services.AddSingleton<IAgentTurnClient>(sp => sp.GetRequiredService<AguiSessionRunner>());
    builder.Services.AddSingleton<IApprovalSessionRunner>(sp => sp.GetRequiredService<AguiSessionRunner>());
    builder.Services.AddSingleton<ApprovalService>();
    builder.Services.AddSingleton<AttachmentService>();
    builder.Services.AddSingleton<CapabilityProfileStore>();
    builder.Services.AddSingleton<CapabilityService>();
    builder.Services.AddSingleton<McpConfigService>();
    builder.Services.AddHostedService<AgentProcessShutdownService>();
    builder.Services.AddTransient<OrbitAgentAuthHandler>();
    builder.Services.AddHttpClient("agent-health", c => c.Timeout = TimeSpan.FromSeconds(2))
        .AddHttpMessageHandler<OrbitAgentAuthHandler>();
    builder.Services.AddHttpClient("agent-attachment", c => c.Timeout = TimeSpan.FromSeconds(45))
        .AddHttpMessageHandler<OrbitAgentAuthHandler>();
    builder.Services.AddHttpClient();
    builder.Services.AddProblemDetails();
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    {
        o.MultipartBodyLengthLimit = 20 * 1024 * 1024;
    });

    var startedAt = DateTimeOffset.UtcNow;
    var app = builder.Build();

    // Sole-owner claim before orphan cleanup so a second Runtime cannot kill the first's agents.
    if (!OrbitLocalAuth.TryClaimRuntimePid(out var pidConflict))
    {
        Log.Fatal(
            "Another Orbit Runtime is already running: {Conflict} Stop it or delete {PidPath}.",
            pidConflict,
            OrbitLocalAuth.TokenPidPath);
        return;
    }

    // Dual secrets: Shell token (disk after bind) vs Agent token (memory only).
    var shellToken = OrbitLocalAuth.CreateToken();
    var agentToken = OrbitLocalAuth.CreateToken();
    var runtimeAuth = app.Services.GetRequiredService<OrbitRuntimeAuth>();
    runtimeAuth.ShellToken = shellToken;
    runtimeAuth.AgentToken = agentToken;
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        OrbitLocalAuth.PersistToken(shellToken);
        Log.Information(
            "Orbit Shell auth token written to {Path} (Shell must send {Header}; Agent uses a separate in-memory token)",
            OrbitLocalAuth.TokenFilePath,
            OrbitLocalAuth.HeaderName);
    });

    app.Use(async (ctx, next) =>
    {
        // /health stays open so Shell can discover a live Runtime before attaching the token.
        if (OrbitLocalAuthGate.IsHealthExempt(ctx.Request.Method, ctx.Request.Path))
        {
            await next().ConfigureAwait(false);
            return;
        }

        ctx.Request.Headers.TryGetValue(OrbitLocalAuth.HeaderName, out var presented);
        var token = presented.ToString();
        // Shell token unlocks all Runtime HTTP. Agent token is only for POST …/agent-meta (stage progress).
        var shellOk = OrbitLocalAuthGate.IsAuthorized(token, shellToken);
        var path = ctx.Request.Path.Value ?? "";
        var agentMetaOk = !shellOk
                          && OrbitLocalAuthGate.IsAgentMetaPath(ctx.Request.Method, path)
                          && OrbitLocalAuthGate.IsAuthorized(token, agentToken);
        if (!shellOk && !agentMetaOk)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(
                    OrbitApiError.Create(
                        OrbitErrorCodes.Unauthorized,
                        "Unauthorized. Start Orbit.Shell or set X-Orbit-Token."))
                .ConfigureAwait(false);
            return;
        }

        await next().ConfigureAwait(false);
    });

    var paths = app.Services.GetRequiredService<RepoPaths>();
    var supervisor = app.Services.GetRequiredService<AgentSupervisor>();
    Log.Information("Orbit Runtime root={Root} logs={Logs}", paths.RepoRoot, OrbitLocalAuth.LogsDirectory);

    var orphans = supervisor.CleanupOrphanAgentProcesses();
    if (orphans > 0)
    {
        Log.Information("Startup: cleaned {Count} orphan agent process(es).", orphans);
    }

    var store = app.Services.GetRequiredService<ISessionStore>();
    var interrupted = store.RecoverInterruptedRuns(agentIdFilter: null, reason: "runtime_restart");
    if (interrupted > 0)
    {
        Log.Information(
            "Startup: closed {Count} session(s) left mid-run (run_interrupted).",
            interrupted);
    }

    // Hard-kill path: release token claim only (graceful stop uses AgentProcessShutdownService).
    AppDomain.CurrentDomain.ProcessExit += (_, _) => OrbitLocalAuth.ReleaseTokenClaim();

    OrbitRuntimeEndpoints.Map(app, startedAt);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Orbit Runtime terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
