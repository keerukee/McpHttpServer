# McpHttpServer

A robust, plug-and-play .NET class library that implements the official **Model Context Protocol (MCP)** using the **Streamable HTTP** transport across all protocol revisions (`2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`, and `2026-07-28`).

Compatible out-of-the-box with **Devin**, **Claude Desktop / Claude Code**, **Cursor**, and **MCP Inspector**.

---

## Features
- **Multi-Version Streamable HTTP Compliance**: Automatically negotiates `protocolVersion` (via `initialize`, `server/discover`, or per-request `_meta`) and validates `MCP-Protocol-Version` headers (`2024-11-05` through `2026-07-28`).
- **Stateless Core, `server/discover` & Header-Based Routing (`2026-07-28`)**: Implements the `server/discover` RPC and per-request `params._meta` negotiation (`SEP-2575`), supports both stateful (`Mcp-Session-Id`) and pure stateless operation, validates `Mcp-Method` and `Mcp-Name` gateway routing headers (`SEP-2243`), and emits `ttlMs` / `cacheScope` list caching directives.
- **Tasks Extension (`io.modelcontextprotocol/tasks` - `SEP-2663`)**: Built-in support for asynchronous tool execution (`params.task` / `_meta["io.modelcontextprotocol/tasks"]`), `tasks/get`, `tasks/list`, `tasks/update`, and `tasks/cancel`.
- **Async & Cancellation Support**: Native support for `async Task<T>`, `ValueTask<T>`, and automatic `CancellationToken` injection from `HttpContext.RequestAborted`.
- **Structured Outputs, Icons & Tool Annotations (`2025-06-18+` / `SEP-973`)**: Supports `Title`, `ReadOnlyHint`, `DestructiveHint`, `IdempotentHint`, `OpenWorldHint`, `outputSchema`, `structuredContent`, custom `icons` metadata (`[McpIcon]` / `IconSrc`), and rich content (`text`, `image`, `audio`, `resource_link`, `resource`).
- **Dynamic Resource Templates**: Automatically exposes parameterized URIs (e.g., `users://{id}/profile`) via `resources/templates/list` and binds URI variables to method parameters on `resources/read`.
- **OAuth 2.0 Protected Resource Discovery (RFC 9728, `SEP-835`, `SEP-991`) & Origin Security**: Built-in `/.well-known/oauth-protected-resource` endpoint (with `client_id_metadata_document_supported`), structured JSON `401` challenges with `WWW-Authenticate` and incremental `scope` parameters for Devin & MCP Inspector, and `AllowedOrigins` `403 Forbidden` enforcement.

---

## Installation

Install the package via NuGet:
```bash
dotnet add package McpHttpServer
```

---

## Quick Start

### 1. Configure your ASP.NET Core App (`Program.cs`)

```csharp
using McpHttpServer;

var builder = WebApplication.CreateBuilder(args);

// Optional: Optimize Kestrel for HTTP/1.1 & HTTP/2 Streams
builder.ConfigureKestrelForMcp();

// Register MCP HTTP Server
builder.Services.AddMcpHttpServer(options =>
{
    options.ServerName = "MyMcpServer";
    options.ServerVersion = "1.1.2";
    options.ServerTitle = "My Enterprise MCP Server";
    options.WebsiteUrl = "https://example.com";
    options.Endpoint = "/mcp"; // Standard Streamable HTTP endpoint
    options.Instructions = "Use these tools to query system metrics and data.";
    options.UseDefaultCors = true;
    options.ListCacheTtlMs = 60000; // Optional caching TTL hint (in ms)

    // Optional: Add a server icon (relative paths like "/icon.png" are auto-resolved to full URLs)
    options.Icons.Add(new McpIcon
    {
        Src = "/icon.png",
        MimeType = "image/png",
        Sizes = new[] { "256x256" },
        Theme = "light"
    });
});

var app = builder.Build();

// Serve static files (e.g. wwwroot/icon.png) before authentication middleware
app.UseStaticFiles();

// Map the /mcp endpoint (auto-discovers all [McpHandler] classes in your assembly)
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
    // 1. Synchronous Tool with Annotations & Shorthand Icon
    [McpTool(
        "calculate_sum",
        "Calculates the sum of two numbers.",
        Title = "Calculate Sum",
        ReadOnlyHint = true,
        IdempotentHint = true,
        IconSrc = "/icons/calc.svg",
        IconMimeType = "image/svg+xml")]
    public int CalculateSum(
        [McpParameter("First number")] int a,
        [McpParameter("Second number")] int b)
    {
        return a + b;
    }

    // 2. Async Tool with Structured Output (auto-generates outputSchema & structuredContent) and CancellationToken
    [McpTool("get_weather", "Returns structured weather data.", UseStructuredOutput = true, ReadOnlyHint = true)]
    [McpIcon("/icons/weather-light.png", mimeType: "image/png", theme: "light", "48x48")]
    [McpIcon("/icons/weather-dark.png", mimeType: "image/png", theme: "dark", "48x48")]
    public async Task<WeatherReport> GetWeatherAsync(
        [McpParameter("City name", EnumValues = new[] { "Seattle", "London", "Tokyo" })] string city,
        CancellationToken ct)
    {
        await Task.Delay(10, ct);
        return new WeatherReport(city, 24.5, "Sunny");
    }

    // 3. Static Resource
    [McpResource("system://status", "System Status", "Returns the current system status.", "application/json")]
    public string GetStatus()
    {
        return "{ \"status\": \"online\", \"uptime\": \"99.9%\" }";
    }

    // 4. Dynamic Resource Template (URI variables bind automatically to method parameters)
    [McpResourceTemplate("users://{id}/profile", "User Profile", "Fetches a user profile by ID.", "application/json")]
    public string GetUserProfile(int id, CancellationToken ct)
    {
        return $"{{ \"id\": {id}, \"role\": \"developer\" }}";
    }

    // 5. Prompt Template
    [McpPrompt("greet_user", "Generates a greeting prompt.", Title = "Greet User")]
    public string Greet([McpArgument("User's display name")] string username)
    {
        return $"Hello {username}, how can I help you today?";
    }
}
```

---

## Advanced Usage

### Returning Rich Content (`text`, `image`, `audio`, `resource_link`, `resource`)

Tools can return `McpContent`, `IEnumerable<McpContent>`, or `McpToolResult` to emit multi-part or rich media payloads:

```csharp
[McpTool("generate_chart", "Returns a chart image and resource link.")]
public IEnumerable<McpContent> GenerateChart([McpParameter] string metric)
{
    yield return McpContent.CreateText($"Generated chart for {metric}");
    yield return McpContent.CreateImage("iVBORw0KGgoAAAANSUhEUg...", "image/png");
    yield return McpContent.CreateResourceLink("system://status", "System Status", "Live status JSON", "application/json");
}
```

### Configuring OAuth 2.0 Discovery & Origin Security

When protecting your MCP server with authentication (`RequireAuthorization = true`), configure `OAuthProtectedResource` so clients like **Devin** and **MCP Inspector** automatically discover your OAuth servers and scopes via `/.well-known/oauth-protected-resource` and `WWW-Authenticate` challenges:

```csharp
builder.Services.AddMcpHttpServer(options =>
{
    options.ServerName = "EnterpriseMcpServer";
    options.RequireAuthorization = true;
    options.AuthorizationPolicy = "MyAuthPolicy"; // Or null for default policy

    // Optional: Restrict browser Origin headers (returns 403 Forbidden if not matched)
    options.AllowedOrigins.Add("https://app.devin.ai");

    // Expose /.well-known/oauth-protected-resource (RFC 9728)
    options.OAuthProtectedResource = new McpOAuthProtectedResourceOptions
    {
        AuthorizationServers = new List<string> { "https://login.microsoftonline.com/<tenant-id>/v2.0" },
        ScopesSupported = new List<string> { "api://my-mcp-server/mcp.tools" }
    };
});
```

### Explicit Handler Registration

Instead of assembly scanning, you can register specific handler types or assemblies explicitly:

```csharp
app.MapMcpHttpServer(typeof(MyAiTools), typeof(AdminTools));
```

---

## Connecting from Devin or MCP Inspector

- **Devin / Devin Local**: Point your MCP server URL to `http://<your-host>:<port>/mcp` (Streamable HTTP).
- **MCP Inspector**:
  1. Run your .NET application.
  2. Launch the inspector: `npx @modelcontextprotocol/inspector`
  3. Select **Streamable HTTP** transport and connect to `http://localhost:<port>/mcp`.
