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
- **Callback-Driven Per-Item Authorization (`[McpAuthorize]`)**: Dynamically filter `tools/list`, `resources/list`, `resources/templates/list`, and `prompts/list` and guard `tools/call`, `resources/read`, and `prompts/get` using your own application callbacks (compiled delegates at startup, per-request deduplication, and optional per-user TTL caching).
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
    options.ServerVersion = "1.2.0";
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

### Per-Tool / Resource / Prompt Authorization (`[McpAuthorize]`)

You can control which Tools, Resources, Resource Templates, and Prompts are visible in `*/list` responses and callable in `tools/call`, `resources/read`, and `prompts/get` by decorating methods (or handler classes) with `[McpAuthorize(nameof(YourCallbackMethod))]`.

- **Open by Default**: Any Tool, Resource, or Prompt **without** `[McpAuthorize]` is open to all callers.
- **Application-Owned Logic**: The library does not enforce any specific auth provider—your callback receives `HttpContext` (or `ClaimsPrincipal`, or no parameters) and returns `bool`, `Task<bool>`, or `ValueTask<bool>`.
- **High Performance**:
  1. **Zero Reflection at Runtime**: Callbacks are compiled into strongly-typed delegates once at startup.
  2. **Per-Request Deduplication**: If 20 tools reference `nameof(CanRead)`, `CanRead` is invoked **only once** per HTTP request.
  3. **Optional Cross-Request TTL Cache**: Set `CacheDurationSeconds = 60` on `[McpAuthorize]` to cache the authorization result per user/session across consecutive requests.
- **Dual Enforcement**: Unauthorized items are hidden from `tools/list`, `resources/list`, `resources/templates/list`, and `prompts/list`. If a caller attempts to invoke a restricted item directly, execution is blocked and the server returns a Tool Execution Error (`isError: true` with `-32001` details) on `tools/call` or a JSON-RPC Error (`-32001`) on `resources/read` and `prompts/get`.

```csharp
using McpHttpServer.Attributes;
using Microsoft.AspNetCore.Http;

[McpHandler]
public class IncidentHandlers
{
    // Application callback for Read-Only access
    public static bool CanRead(HttpContext? context)
    {
        var role = context?.Request.Headers["X-Access-Mode"].ToString();
        return role == "Read" || role == "CURD";
    }

    // Application callback for full CURD access (supports async Task<bool> as well)
    public static Task<bool> CanCurdAsync(HttpContext? context)
    {
        var role = context?.Request.Headers["X-Access-Mode"].ToString();
        return Task.FromResult(role == "CURD");
    }

    // Public tool (no [McpAuthorize] decorator -> visible to everyone)
    [McpTool("health_check", "Public health check tool")]
    public string HealthCheck() => "Healthy";

    // Visible and callable only when CanRead returns true
    [McpAuthorize(nameof(CanRead))]
    [McpTool("get_incident", "Gets an incident by ID", ReadOnlyHint = true)]
    public string GetIncident([McpParameter] string id) => $"Incident {id}";

    // Visible and callable only when CanCurdAsync returns true (with optional 60s per-user cache)
    [McpAuthorize(nameof(CanCurdAsync), CacheDurationSeconds = 60)]
    [McpTool("delete_incident", "Deletes an incident by ID", DestructiveHint = true)]
    public string DeleteIncident([McpParameter] string id) => $"Deleted {id}";
}
```

#### Supported `[McpAuthorize]` Callback Signatures & Usages
- **Same-Class or External Class**:
  - `[McpAuthorize(nameof(CanRead))]` — invokes `CanRead` on the same handler class (static or instance method).
  - `[McpAuthorize(typeof(MyAuthService), nameof(MyAuthService.CanWrite))]` — invokes `CanWrite` on `MyAuthService` (resolved via DI or `ActivatorUtilities`).
- **Class-Level or Method-Level**:
  - Apply `[McpAuthorize]` to an entire `[McpHandler]` class to protect all tools/resources/prompts in that class, or apply it to individual methods. Multiple `[McpAuthorize]` attributes on the same item are evaluated with logical `AND`.
- **Supported Callback Parameters & Return Types**:
  - Parameters: `(HttpContext? context)`, `(ClaimsPrincipal? user)`, or parameterless `()`
  - Return types: `bool`, `Task<bool>`, or `ValueTask<bool>`

---

### Attributes & Options Reference

| Attribute | Target | Description |
| :--- | :--- | :--- |
| `[McpHandler]` | Class | Marks a class for automatic discovery of tools, resources, and prompts. |
| `[McpTool(name, description)]` | Method | Registers an MCP Tool (`Title`, `ReadOnlyHint`, `DestructiveHint`, `IdempotentHint`, `OpenWorldHint`, `UseStructuredOutput`, `IconSrc`, `IconMimeType`). |
| `[McpParameter(description)]` | Parameter | Adds description and optional `EnumValues` to a tool or URI template parameter. |
| `[McpResource(uri, name, desc, mime)]` | Method | Registers a static MCP Resource (or dynamic template if `uri` contains `{param}`). |
| `[McpResourceTemplate(uriTemplate, ...)]` | Method | Registers a parameterized URI template (`resources/templates/list`). |
| `[McpPrompt(name, description)]` | Method | Registers an MCP Prompt template. |
| `[McpArgument(description)]` | Parameter | Adds description metadata to a prompt parameter. |
| `[McpIcon(src, mimeType, theme, ...)]` | Method | Attaches one or more light/dark/sized icons to a tool, resource, or prompt (`SEP-973`). |
| `[McpAuthorize(callbackName)]` | Class / Method | Guards listing (`*/list`) and execution (`tools/call`, `resources/read`, `prompts/get`) via an application callback. |

---

### Protocol Version Negotiation & Backward Compatibility

`McpHttpServer` supports all 5 official MCP revisions simultaneously out of the box without any manual configuration:
- **Modern Stateless Lifecycle (`2026-07-28`)**: Handles `server/discover` (`DiscoverResult` with `resultType: "complete"`, `supportedVersions`, `ttlMs`, `cacheScope`, and `_meta["io.modelcontextprotocol/serverInfo"]`), per-request `_meta["io.modelcontextprotocol/protocolVersion"]`, `Mcp-Method` / `Mcp-Name` routing header validation (`SEP-2243`), and asynchronous tasks (`io.modelcontextprotocol/tasks` / `SEP-2663`).
- **Stateful Handshake Lifecycle (`2025-11-25`, `2025-06-18`, `2025-03-26`, `2024-11-05`)**: Handles `initialize` and `notifications/initialized`, echoes client-requested handshake versions, and automatically negotiates `2025-11-25` (`LATEST_WITH_INITIALIZE`) on `initialize` for full compatibility with **Devin Desktop / Devin Local**, **Claude Desktop / Claude Code**, **Cursor**, and **MCP Inspector**.

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
