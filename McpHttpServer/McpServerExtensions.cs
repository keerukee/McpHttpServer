using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using McpHttpServer.Attributes;

namespace McpHttpServer;

/// <summary>
/// Extension methods for configuring MCP Streamable HTTP Server in ASP.NET Core applications.
/// </summary>
public static class McpServerExtensions
{
    /// <summary>
    /// Adds MCP HTTP Server services to the service collection.
    /// </summary>
    public static IServiceCollection AddMcpHttpServer(
        this IServiceCollection services,
        Action<McpServerOptions>? configureOptions = null)
    {
        var options = new McpServerOptions();
        configureOptions?.Invoke(options);

        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        services.AddSingleton<McpRegistry>();
        services.AddSingleton<McpRequestProcessor>();
        services.AddSingleton<ConcurrentDictionary<string, HttpSession>>();

        if (options.UseDefaultCors)
        {
            services.AddCors(corsOptions =>
            {
                corsOptions.AddPolicy("McpCors", policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .WithExposedHeaders("Mcp-Session-Id");
                });
            });
        }

        return services;
    }

    /// <summary>
    /// Configures Kestrel for SSE compatibility (HTTP/1.1) if needed for streams.
    /// </summary>
    public static WebApplicationBuilder ConfigureKestrelForMcp(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureEndpointDefaults(listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });

        return builder;
    }

    /// <summary>
    /// Maps MCP HTTP Server endpoints and auto-discovers handlers from the entry assembly.
    /// </summary>
    public static IEndpointConventionBuilder MapMcpHttpServer(this IEndpointRouteBuilder endpoints)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var callingAssembly = Assembly.GetCallingAssembly();
        
        var assemblies = new HashSet<Assembly>();
        if (entryAssembly != null) assemblies.Add(entryAssembly);
        assemblies.Add(callingAssembly);

        return endpoints.MapMcpHttpServerCore(assemblies.ToArray());
    }

    /// <summary>
    /// Maps MCP HTTP Server endpoints and registers handlers from the specified types.
    /// </summary>
    public static IEndpointConventionBuilder MapMcpHttpServer(this IEndpointRouteBuilder endpoints, params Type[] handlerTypes)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<McpRegistry>();
        
        if (handlerTypes.Length > 0)
            registry.RegisterFromTypes(handlerTypes);

        return endpoints.MapMcpHttpServerCore();
    }

    /// <summary>
    /// Maps MCP HTTP Server endpoints and auto-discovers handlers from specified assemblies.
    /// </summary>
    public static IEndpointConventionBuilder MapMcpHttpServer(this IEndpointRouteBuilder endpoints, params Assembly[] assemblies)
    {
        return endpoints.MapMcpHttpServerCore(assemblies);
    }

    private static IEndpointConventionBuilder MapMcpHttpServerCore(this IEndpointRouteBuilder endpoints, Assembly[]? assemblies = null)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<McpServerOptions>();
        var registry = endpoints.ServiceProvider.GetRequiredService<McpRegistry>();
        var processor = endpoints.ServiceProvider.GetRequiredService<McpRequestProcessor>();
        var sessions = endpoints.ServiceProvider.GetRequiredService<ConcurrentDictionary<string, HttpSession>>();

        // Auto-discover handlers from assemblies
        if (assemblies != null && assemblies.Length > 0)
        {
            foreach (var assembly in assemblies)
            {
                var handlerTypes = assembly.GetTypes()
                    .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<McpHandlerAttribute>() != null)
                    .ToArray();

                if (handlerTypes.Length > 0)
                {
                    registry.RegisterFromTypes(handlerTypes);
                }
            }
        }

        if (endpoints is IApplicationBuilder app)
        {
            if (options.UseDefaultCors)
            {
                app.UseCors("McpCors");
            }

            if (options.EnableHttpLogging)
            {
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(options.Endpoint))
                    {
                        Console.WriteLine($"[MCP HTTP] {context.Request.Method} {context.Request.Path}");
                    }
                    await next();
                });
            }
        }

        var group = endpoints.MapGroup(options.Endpoint);

        if (options.UseDefaultCors)
        {
            group.RequireCors("McpCors");
        }

        // Apply Authorization if configured
        if (options.RequireAuthorization)
        {
            if (string.IsNullOrEmpty(options.AuthorizationPolicy))
                group.RequireAuthorization();
            else
                group.RequireAuthorization(options.AuthorizationPolicy);
        }

        // GET Endpoint - Used to open an SSE stream
        group.MapGet("", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            if (ctx.Request.Protocol == "HTTP/1.1")
                ctx.Response.Headers.Connection = "keep-alive";

            string sessionId = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
            ctx.Response.Headers["Mcp-Session-Id"] = sessionId;

            var session = new HttpSession(ctx.Response);
            sessions[sessionId] = session;

            if (options.EnableLogging)
                Console.WriteLine($"[MCP] Stream Connected: {sessionId}");

            // Optional: Send an initial endpoint event for backwards compatibility or debugging
            var request = ctx.Request;
            var baseUrl = $"{request.Scheme}://{request.Host}";
            string endpointUri = $"{baseUrl}{options.Endpoint}";
            await session.SendEventAsync("endpoint", endpointUri);

            var tcs = new TaskCompletionSource();
            using var registration = ctx.RequestAborted.Register(() => tcs.TrySetResult());

            try
            {
                await tcs.Task;
            }
            finally
            {
                sessions.TryRemove(sessionId, out _);
                session.Dispose();
                if (options.EnableLogging)
                    Console.WriteLine($"[MCP] Stream Disconnected: {sessionId}");
            }
        });

        // POST Endpoint - Processes JSON-RPC requests
        group.MapPost("", async (HttpContext ctx) =>
        {
            string? sessionId = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
            
            using var reader = new StreamReader(ctx.Request.Body);
            var requestJson = await reader.ReadToEndAsync();
            
            bool isInitialize = requestJson.Contains("\"method\":\"initialize\"") || requestJson.Contains("\"method\": \"initialize\"");

            if (string.IsNullOrEmpty(sessionId) && isInitialize)
            {
                sessionId = Guid.NewGuid().ToString();
            }

            // The processor handles single or batch, returning JSON string
            var responseJson = processor.Process(requestJson);

            // Add the session ID header if one exists
            if (!string.IsNullOrEmpty(sessionId))
            {
                ctx.Response.Headers["Mcp-Session-Id"] = sessionId;
            }

            if (responseJson == null)
            {
                // It was a notification
                return Results.Accepted();
            }

            // Streamable HTTP says we MUST return application/json (or text/event-stream).
            // We'll return application/json for the immediate response.
            return Results.Content(responseJson, "application/json");
        });

        // DELETE Endpoint - Terminate session
        group.MapDelete("", (HttpContext ctx) =>
        {
            var sessionId = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
            if (!string.IsNullOrEmpty(sessionId))
            {
                if (sessions.TryRemove(sessionId, out var session))
                {
                    session.Dispose();
                    if (options.EnableLogging)
                        Console.WriteLine($"[MCP] Session Explicitly Terminated: {sessionId}");
                }
            }
            return Results.Ok();
        });

        // Print startup info
        Console.WriteLine("\n========================================");
        Console.WriteLine($"{options.ServerName} v{options.ServerVersion}");
        Console.WriteLine("========================================");
        Console.WriteLine($"Endpoint: {options.Endpoint} (Streamable HTTP)");
        Console.WriteLine($"Authorization Required: {options.RequireAuthorization}");
        Console.WriteLine($"Tools: {registry.Tools.Count}");
        Console.WriteLine($"Resources: {registry.Resources.Count}");
        Console.WriteLine($"Prompts: {registry.Prompts.Count}");
        Console.WriteLine("========================================\n");

        return group;
    }
}
