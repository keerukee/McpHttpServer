# McpHttpServer

A robust, plug-and-play .NET class library that implements the official **Model Context Protocol (MCP)** using the **Streamable HTTP** transport across all protocol revisions (`2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, and `2026-07-28`).

Compatible out-of-the-box with **Devin**, **Claude Desktop / Claude Code**, **Cursor**, and **MCP Inspector**.

## Features
- **Multi-Version Streamable HTTP Compliance**: Automatically negotiates `protocolVersion` (via `initialize`, `server/discover`, or per-request `_meta`) and validates `MCP-Protocol-Version` headers (`2024-11-05` through `2026-07-28`).
- **Stateless Core, `server/discover` & Header-Based Routing (`2026-07-28`)**: Implements the `server/discover` RPC and per-request `params._meta` negotiation (`SEP-2575`), supports both stateful (`Mcp-Session-Id`) and pure stateless operation, validates `Mcp-Method` and `Mcp-Name` gateway routing headers (`SEP-2243`), and emits `ttlMs` / `cacheScope` list caching directives.
- **Tasks Extension (`io.modelcontextprotocol/tasks` - `SEP-2663`)**: Built-in support for asynchronous tool execution (`params.task` / `_meta["io.modelcontextprotocol/tasks"]`), `tasks/get`, `tasks/list`, `tasks/update`, and `tasks/cancel`.
- **Async & Cancellation Support**: Native support for `async Task<T>`, `ValueTask<T>`, and automatic `CancellationToken` injection from `HttpContext.RequestAborted`.
- **Structured Outputs, Icons & Tool Annotations (`2025-06-18+` / `SEP-973`)**: Supports `Title`, `ReadOnlyHint`, `DestructiveHint`, `IdempotentHint`, `OpenWorldHint`, `outputSchema`, `structuredContent`, custom `icons` metadata (`[McpIcon]` / `IconSrc`), and rich content (`text`, `image`, `audio`, `resource_link`, `resource`).
- **Dynamic Resource Templates**: Automatically exposes parameterized URIs (e.g., `users://{id}/profile`) via `resources/templates/list` and binds URI variables to method parameters on `resources/read`.
- **OAuth 2.0 Protected Resource Discovery (RFC 9728, `SEP-835`, `SEP-991`) & Origin Security**: Built-in `/.well-known/oauth-protected-resource` endpoint (with `client_id_metadata_document_supported`), structured JSON `401` challenges with `WWW-Authenticate` and incremental `scope` parameters for Devin & MCP Inspector, and `AllowedOrigins` `403 Forbidden` enforcement.

## Installation

Install the package via NuGet:
```bash
dotnet add package McpHttpServer
```

## Quick Start

### 1. Configure your ASP.NET Core App
In your `Program.cs`, add the MCP Server services and map the endpoints:

```csharp
using McpHttpServer;

var builder = WebApplication.CreateBuilder(args);

// Optional: Optimize Kestrel for HTTP/1.1 & HTTP/2 Streams
builder.ConfigureKestrelForMcp();

// Add MCP Server
builder.Services.AddMcpHttpServer(options =>
{
    options.ServerName = "MyMcpServer";
    options.ServerVersion = "1.1.0";
    options.Endpoint = "/mcp"; // Standard Streamable HTTP endpoint
    options.Instructions = "Use these tools to query system metrics and data.";
    options.RequireAuthorization = false; // Set to true to require auth
    options.UseDefaultCors = true;
    options.ListCacheTtlMs = 60000; // Optional TTL hint for 2026-07-28 clients
});

var app = builder.Build();

// Map the /mcp endpoint
app.MapMcpHttpServer();

app.Run();
```

### 2. Create Tools, Resources, Resource Templates, and Prompts
Decorate any class with `[McpHandler]` and use `[McpTool]`, `[McpResource]`, `[McpResourceTemplate]`, and `[McpPrompt]`:

```csharp
using McpHttpServer;
using McpHttpServer.Attributes;

public record WeatherReport(string City, double TemperatureC, string Condition);

[McpHandler]
public class MyAiTools
{
    [McpTool("calculate_sum", "Calculates the sum of two numbers.", Title = "Calculate Sum", ReadOnlyHint = true, IdempotentHint = true)]
    public int CalculateSum([McpParameter] int a, [McpParameter] int b)
    {
        return a + b;
    }

    [McpTool("get_weather", "Returns structured weather data.", UseStructuredOutput = true, ReadOnlyHint = true)]
    public async Task<WeatherReport> GetWeatherAsync([McpParameter("City name")] string city, CancellationToken ct)
    {
        await Task.Delay(10, ct);
        return new WeatherReport(city, 24.5, "Sunny");
    }

    [McpResource("system://status", "System Status", "Returns the current system status.", "application/json")]
    public string GetStatus()
    {
        return "{ \"status\": \"online\", \"uptime\": \"99.9%\" }";
    }

    [McpResourceTemplate("users://{id}/profile", "User Profile", "Fetches a user profile by ID.", "application/json")]
    public string GetUserProfile(int id)
    {
        return $"{{ \"id\": {id}, \"role\": \"developer\" }}";
    }

    [McpPrompt("greet_user", "Generates a greeting prompt.")]
    public string Greet([McpArgument] string username)
    {
        return $"Hello {username}, how can I help you today?";
    }
}
```

## Connecting from Devin or MCP Inspector

## Testing with MCP Inspector

You can easily test your server using the official MCP Inspector.
1. Run your .NET application.
2. Launch the inspector: `npx @modelcontextprotocol/inspector`
3. Select **HTTP** transport and connect to `http://localhost:<port>/mcp`.
