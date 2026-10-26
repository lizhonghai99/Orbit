using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Orbit.Agents.Hosting.Mcp;
using Orbit.Contracts;
using Serilog;

namespace Orbit.Agents.Hosting;

/// <summary>
/// Thin AG-UI host. Agents register their own services/tools (browser, MCP, skills, …).
/// </summary>
public static class AgentHost
{
    public static WebApplication BuildAguiHost(
        string[] args,
        string agentId,
        Action<WebApplicationBuilder> configureServices,
        Action<WebApplication>? configureEndpoints = null,
        Func<IServiceProvider, IReadOnlyDictionary<string, string>?>? healthExtras = null)
    {
        OrbitAgentLogging.Bootstrap(agentId);

        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseSerilog();
        builder.Services.AddAGUIServer();
        builder.Services.AddProblemDetails();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = 20 * 1024 * 1024; // 20 MB
        });
        builder.Services.AddSingleton(new AttachmentStore(agentId));
        configureServices(builder);

        var app = builder.Build();
        OrbitTelemetry.EnsureConsoleListener(app.Logger, agentId);

        // Require Runtime token when Agent was started with ORBIT_RUNTIME_TOKEN (local threat model).
        var expectedToken = Environment.GetEnvironmentVariable("ORBIT_RUNTIME_TOKEN");
        var authRequired = !string.IsNullOrWhiteSpace(expectedToken);
        if (authRequired)
        {
            app.Use(async (ctx, next) =>
            {
                if (HttpMethods.IsGet(ctx.Request.Method)
                    && ctx.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                if (!ctx.Request.Headers.TryGetValue(OrbitLocalAuth.HeaderName, out var presented)
                    || !OrbitLocalAuthGate.IsAuthorized(presented.ToString(), expectedToken!))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(OrbitApiError.Create(
                            OrbitErrorCodes.Unauthorized,
                            "Unauthorized. Runtime must send X-Orbit-Token."))
                        .ConfigureAwait(false);
                    return;
                }

                await next().ConfigureAwait(false);
            });
        }

        // Stamp ambient run/session/workspace for telemetry + tool roots (Runtime sends these on AG-UI calls).
        app.Use(async (ctx, next) =>
        {
            var runId = ctx.Request.Headers[OrbitWorkspaceHeaders.RunId].FirstOrDefault();
            var sessionId = ctx.Request.Headers[OrbitWorkspaceHeaders.SessionId].FirstOrDefault();
            var workspace = ctx.Request.Headers[OrbitWorkspaceHeaders.Workspace].FirstOrDefault();
            var workspaceId = ctx.Request.Headers[OrbitWorkspaceHeaders.WorkspaceId].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(runId)
                && string.IsNullOrWhiteSpace(sessionId)
                && string.IsNullOrWhiteSpace(workspace))
            {
                await next().ConfigureAwait(false);
                return;
            }

            // Decode b64 path (non-ASCII safe), then validate existence + optional ORBIT_WORKSPACE pin.
            if (!string.IsNullOrWhiteSpace(workspace))
            {
                var decoded = OrbitWorkspaceHeaders.DecodePath(workspace);
                if (string.IsNullOrWhiteSpace(decoded))
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(OrbitApiError.Create(
                            OrbitErrorCodes.BadRequest,
                            "invalid_workspace_header"))
                        .ConfigureAwait(false);
                    return;
                }

                try
                {
                    var full = Path.GetFullPath(decoded.Trim());
                    if (!Directory.Exists(full))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await ctx.Response.WriteAsJsonAsync(OrbitApiError.Create(
                                OrbitErrorCodes.WorkspaceMissing,
                                $"workspace_missing: {full}",
                                new Dictionary<string, string> { ["path"] = full }))
                            .ConfigureAwait(false);
                        return;
                    }

                    var pinned = Environment.GetEnvironmentVariable("ORBIT_WORKSPACE");
                    if (!string.IsNullOrWhiteSpace(pinned))
                    {
                        var pinFull = Path.GetFullPath(pinned.Trim())
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        var reqFull = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (!string.Equals(pinFull, reqFull, StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await ctx.Response.WriteAsJsonAsync(OrbitApiError.Create(
                                    OrbitErrorCodes.WorkspaceMismatch,
                                    $"workspace_not_allowed: {full}",
                                    new Dictionary<string, string>
                                    {
                                        ["path"] = full,
                                        ["expected"] = pinFull
                                    }))
                                .ConfigureAwait(false);
                            return;
                        }
                    }

                    workspace = full;
                }
                catch
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(OrbitApiError.Create(
                            OrbitErrorCodes.BadRequest,
                            "invalid_workspace_header"))
                        .ConfigureAwait(false);
                    return;
                }
            }

            using (OrbitRunContext.Push(runId, sessionId, workspace, workspaceId))
            {
                await next().ConfigureAwait(false);
            }
        });

        var agent = app.Services.GetRequiredService<AIAgent>();
        var skillInfos = SkillCatalog.Discover(agentId, builder.Environment.ContentRootPath);
        var skillSummaries = SkillCatalog.ToSummaries(skillInfos);

        app.MapGet("/health", (IServiceProvider sp) =>
        {
            var chat = ChatClientFactory.DescribeMode();
            var contentRoot = builder.Environment.ContentRootPath;
            var tools = healthExtras?.Invoke(sp)?.ToDictionary(kv => kv.Key, kv => kv.Value);
            return Results.Ok(new AgentProcessHealthDto
            {
                Status = chat is "missing_key" ? "degraded" : "ok",
                AgentId = agentId,
                Protocol = "ag-ui",
                AguiPath = "/ag-ui",
                Ready = chat is not "missing_key",
                Chat = chat,
                Model = ChatClientFactory.DescribeModel(),
                Reasoning = OrbitReasoning.DescribeForHealth(),
                Harness = HarnessProfile.Describe(agentId, contentRoot),
                Skills = skillSummaries,
                Tools = tools,
                Workspace = Environment.GetEnvironmentVariable("ORBIT_WORKSPACE"),
                WorkspaceId = Environment.GetEnvironmentVariable("ORBIT_WORKSPACE_ID"),
                AuthRequired = authRequired
            });
        });

        app.MapGet("/skills", () => Results.Ok(skillSummaries));

        app.MapGet("/tools", () => Results.Ok(AgentToolSchemaCatalog.Get(agentId)));

        // Capability enforcement profile (disable lists). Catalog/schema are never rebuilt here.
        app.MapGet("/capabilities", () => Results.Ok(CapabilityProfile.Instance.Snapshot()));
        app.MapPut("/capabilities", (CapabilityPatchRequest? body) =>
        {
            CapabilityProfile.Instance.Apply(body ?? new CapabilityPatchRequest());
            return Results.Ok(CapabilityProfile.Instance.Snapshot());
        });

        // MCP status snapshot only (servers were connected at process start).
        app.MapGet("/mcp", (IServiceProvider sp) =>
        {
            var host = sp.GetService<McpToolHost>() ?? McpToolHost.TryGet(agentId);
            if (host is null)
            {
                return Results.Ok(new McpStatusResponse { Configured = false, Servers = [] });
            }

            return Results.Ok(new McpStatusResponse
            {
                Configured = host.Configured,
                Servers = host.Servers
            });
        });

        app.MapGet("/telemetry/invocations", (DateTimeOffset? since, string? runId, string? sessionId, bool? claim) =>
        {
            if (claim == true)
            {
                return Results.Ok(InvocationTelemetryStore.Claim(runId, sessionId, since));
            }

            return Results.Ok(InvocationTelemetryStore.Snapshot(since, runId, sessionId));
        });

        app.MapGet("/attachments", (AttachmentStore store) => Results.Ok(store.ListRecent()));

        app.MapPost("/attachments", async (HttpRequest request, AttachmentStore store, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.Json(
                    OrbitApiError.Create(OrbitErrorCodes.BadRequest, "multipart/form-data required"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.Json(
                    OrbitApiError.Create(OrbitErrorCodes.BadRequest, "file is required"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (file.Length > 20 * 1024 * 1024)
            {
                return Results.Json(
                    OrbitApiError.Create(OrbitErrorCodes.BadRequest, "file exceeds 20 MB limit"),
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            await using var stream = file.OpenReadStream();
            var info = await store.SaveAsync(file.FileName, file.ContentType ?? "application/octet-stream", stream, ct)
                .ConfigureAwait(false);
            return Results.Ok(info);
        });

        app.MapAGUIServer("/ag-ui", agent);
        configureEndpoints?.Invoke(app);
        return app;
    }
}
