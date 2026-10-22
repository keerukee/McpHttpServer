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
        services.AddSingleton<McpTaskStore>();
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
                          .WithExposedHeaders("Mcp-Session-Id", "MCP-Protocol-Version", "Mcp-Method", "Mcp-Name");
                });
            });
        }

        return services;
    }

    /// <summary>
    /// Configures Kestrel for SSE compatibility (HTTP/1.1 and HTTP/2) if needed for streams.
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
    /// Maps MCP HTTP Server endpoints and auto-discovers handlers from the entry and calling assemblies.
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
            registry.RegisterFromAssemblies(assemblies);
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

            // Ensure 401 Unauthorized responses on the MCP endpoint return valid JSON OAuth error payloads
            // and WWW-Authenticate discovery headers for clients like Devin and MCP Inspector.
            if (options.RequireAuthorization)
            {
                app.Use(async (context, next) =>
                {
                    await next();

                    if (context.Request.Path.StartsWithSegments(options.Endpoint) &&
                        context.Response.StatusCode == StatusCodes.Status401Unauthorized &&
                        !context.Response.HasStarted)
                    {
                        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
                        if (!context.Response.Headers.ContainsKey("WWW-Authenticate"))
                        {
                            var wwwAuth = $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
                            if (options.OAuthProtectedResource?.ScopesSupported is { Count: > 0 } scopes)
                            {
                                wwwAuth += $", scope=\"{string.Join(" ", scopes)}\"";
                            }
                            context.Response.Headers["WWW-Authenticate"] = wwwAuth;
                        }

                        context.Response.ContentType = "application/json";
                        var errorJson = JsonSerializer.Serialize(new
                        {
                            error = "unauthorized",
                            error_description = "Authentication is required to access this MCP server."
                        });
                        await context.Response.WriteAsync(errorJson);
                    }
                });
            }
        }

        // Map RFC 9728 OAuth 2.0 Protected Resource Metadata endpoint (/.well-known/oauth-protected-resource)
        // Used by Devin, Claude, and MCP Inspector during OAuth discovery flows.
        if (options.RequireAuthorization || options.OAuthProtectedResource != null)
        {
            endpoints.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx) =>
            {
                var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                var oauthOpts = options.OAuthProtectedResource;
                var metadata = new
                {
                    resource = oauthOpts?.Resource ?? $"{baseUrl}{options.Endpoint}",
                    authorization_servers = oauthOpts?.AuthorizationServers ?? new List<string>(),
                    scopes_supported = oauthOpts?.ScopesSupported ?? new List<string>(),
                    bearer_methods_supported = oauthOpts?.BearerMethodsSupported ?? new List<string> { "header" },
                    client_id_metadata_document_supported = oauthOpts?.ClientIdMetadataDocumentSupported ?? true
                };
                return Results.Json(metadata);
            }).AllowAnonymous();
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

        // GET Endpoint - Used to open an SSE stream for server-to-client messages
        group.MapGet("", async (HttpContext ctx) =>
        {
            if (!ValidateOrigin(ctx, options, out var originResult))
                return originResult!;

            if (!ValidateProtocolVersionHeader(ctx, options, out var versionResult, out var negotiatedVersion))
                return versionResult!;

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Response.Headers["MCP-Protocol-Version"] = negotiatedVersion;

            if (ctx.Request.Protocol == "HTTP/1.1")
                ctx.Response.Headers.Connection = "keep-alive";

            string sessionId = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
            if (!options.StatelessMode)
            {
                ctx.Response.Headers["Mcp-Session-Id"] = sessionId;
            }

            string? lastEventId = ctx.Request.Headers["Last-Event-ID"].FirstOrDefault();
            var session = new HttpSession(ctx.Response, lastEventId);
            sessions[sessionId] = session;

            if (options.EnableLogging)
                Console.WriteLine($"[MCP] Stream Connected: {sessionId}");

            // Send initial endpoint event to flush headers immediately
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

            return Results.Empty;
        });

        // POST Endpoint - Processes JSON-RPC requests (Streamable HTTP)
        group.MapPost("", async (HttpContext ctx) =>
        {
            if (!ValidateOrigin(ctx, options, out var originResult))
                return originResult!;

            if (!ValidateProtocolVersionHeader(ctx, options, out var versionResult, out var negotiatedVersion))
                return versionResult!;

            string? sessionId = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();

            using var reader = new StreamReader(ctx.Request.Body);
            var requestJson = await reader.ReadToEndAsync(ctx.RequestAborted);

            // Validate Mcp-Method and Mcp-Name routing headers if provided (MCP 2026-07-28 / SEP-2243)
            if (options.ValidateRoutingHeaders)
            {
                var mcpMethodHeader = ctx.Request.Headers["Mcp-Method"].FirstOrDefault();
                var mcpNameHeader = ctx.Request.Headers["Mcp-Name"].FirstOrDefault();
                if (!processor.ValidateRoutingHeaders(requestJson, mcpMethodHeader, mcpNameHeader, out var headerError))
                {
                    return Results.Json(new
                    {
                        jsonrpc = "2.0",
                        id = (object?)null,
                        error = new { code = -32600, message = headerError }
                    }, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            bool isInitialize = requestJson.Contains("\"method\":\"initialize\"") || requestJson.Contains("\"method\": \"initialize\"");

            if (!options.StatelessMode && string.IsNullOrEmpty(sessionId) && isInitialize)
            {
                sessionId = Guid.NewGuid().ToString();
            }

            // Process single or batch JSON-RPC request asynchronously
            var responseJson = await processor.ProcessAsync(requestJson, ctx.RequestAborted);

            // If MCP-Protocol-Version header was not sent, check if JSON body negotiated a version via _meta or initialize
            if (!ctx.Request.Headers.ContainsKey("MCP-Protocol-Version") &&
                TryExtractBodyProtocolVersion(requestJson, options, out var bodyVersion))
            {
                negotiatedVersion = bodyVersion!;
            }
            else if (isInitialize && !McpRequestProcessor.HasInitializeHandshake(negotiatedVersion))
            {
                negotiatedVersion = McpRequestProcessor.NegotiateInitializeVersion(negotiatedVersion, options);
            }

            ctx.Response.Headers["MCP-Protocol-Version"] = negotiatedVersion;

            // Add the session ID header if one exists and not in pure stateless mode
            if (!options.StatelessMode && !string.IsNullOrEmpty(sessionId))
            {
                ctx.Response.Headers["Mcp-Session-Id"] = sessionId;
            }

            if (responseJson == null)
            {
                // Notification or client response -> 202 Accepted
                return Results.Accepted();
            }

            // If client explicitly requests ONLY text/event-stream on POST, format as SSE event
            var acceptHeader = ctx.Request.Headers.Accept.ToString();
            if (!string.IsNullOrEmpty(acceptHeader) &&
                acceptHeader.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) &&
                !acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
                !acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers.CacheControl = "no-cache";
                var ssePayload = $"id: 1\nevent: message\ndata: {responseJson}\n\n";
                return Results.Content(ssePayload, "text/event-stream");
            }

            return Results.Content(responseJson, "application/json");
        });

        // DELETE Endpoint - Terminate session
        group.MapDelete("", (HttpContext ctx) =>
        {
            if (!ValidateOrigin(ctx, options, out var originResult))
                return originResult!;

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
        Console.WriteLine($"Protocol Versions: {string.Join(", ", options.SupportedProtocolVersions)}");
        Console.WriteLine($"Stateless Mode: {options.StatelessMode}");
        Console.WriteLine($"Authorization Required: {options.RequireAuthorization}");
        Console.WriteLine($"Tools: {registry.Tools.Count}");
        Console.WriteLine($"Resources: {registry.Resources.Count} (Templates: {registry.ResourceTemplates.Count})");
        Console.WriteLine($"Prompts: {registry.Prompts.Count}");
        Console.WriteLine("========================================\n");

        return group;
    }

    private static bool ValidateOrigin(HttpContext ctx, McpServerOptions options, out IResult? errorResult)
    {
        errorResult = null;
        if (options.AllowedOrigins.Count == 0)
            return true;

        if (ctx.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            var origin = originValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(origin) && !options.AllowedOrigins.Contains(origin))
            {
                errorResult = Results.Json(new
                {
                    jsonrpc = "2.0",
                    id = (object?)null,
                    error = new { code = -32600, message = $"Origin '{origin}' is not allowed." }
                }, statusCode: StatusCodes.Status403Forbidden);
                return false;
            }
        }

        return true;
    }

    private static bool ValidateProtocolVersionHeader(
        HttpContext ctx,
        McpServerOptions options,
        out IResult? errorResult,
        out string negotiatedVersion)
    {
        errorResult = null;
        negotiatedVersion = options.ProtocolVersion;

        if (ctx.Request.Headers.TryGetValue("MCP-Protocol-Version", out var versionValues))
        {
            var requestedVersion = versionValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(requestedVersion))
            {
                if (options.ValidateProtocolVersionHeader && !options.SupportedProtocolVersions.Contains(requestedVersion))
                {
                    errorResult = Results.Json(new
                    {
                        jsonrpc = "2.0",
                        id = (object?)null,
                        error = new
                        {
                            code = -32600,
                            message = $"Unsupported MCP-Protocol-Version '{requestedVersion}'. Supported versions: {string.Join(", ", options.SupportedProtocolVersions)}"
                        }
                    }, statusCode: StatusCodes.Status400BadRequest);
                    return false;
                }

                negotiatedVersion = requestedVersion;
            }
        }

        return true;
    }

    private static bool TryExtractBodyProtocolVersion(string json, McpServerOptions options, out string? version)
    {
        version = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("params", out var p) ||
                p.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool isInitMethod = root.TryGetProperty("method", out var methodProp) &&
                                string.Equals(methodProp.GetString(), "initialize", StringComparison.Ordinal);

            if (p.TryGetProperty("_meta", out var meta) &&
                meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var metaVer))
            {
                var v = metaVer.GetString();
                if (!string.IsNullOrEmpty(v) && options.SupportedProtocolVersions.Contains(v))
                {
                    version = isInitMethod ? McpRequestProcessor.NegotiateInitializeVersion(v, options) : v;
                    return true;
                }
            }

            if (p.TryGetProperty("protocolVersion", out var initVer))
            {
                var v = initVer.GetString();
                if (!string.IsNullOrEmpty(v))
                {
                    version = McpRequestProcessor.NegotiateInitializeVersion(v, options);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return false;
    }
}
