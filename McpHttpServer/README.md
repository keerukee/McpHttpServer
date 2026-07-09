# McpHttpServer

A robust, plug-and-play .NET class library that implements the official **Model Context Protocol (MCP)** using the new **Streamable HTTP** transport (2025-03-26 specification).

This library allows you to effortlessly host MCP Tools, Resources, and Prompts directly inside your ASP.NET Core applications. It handles all JSON-RPC parsing, SSE stream management, session IDs, and routing automatically.

## Features
- **Streamable HTTP Compliant**: Fully implements the latest standard for HTTP/SSE based MCP communication.
- **Attribute-Based Routing**: Auto-discovers your tools, resources, and prompts using simple `[McpTool]`, `[McpResource]`, and `[McpPrompt]` attributes.
- **Built-in Session Management**: Automatically handles `Mcp-Session-Id` headers and maintains SSE streams for unprompted server notifications.
- **Secure**: Native integration with ASP.NET Core Authentication and Authorization (`[Authorize]`).
- **CORS Support**: Easy toggles for Cross-Origin Resource Sharing to allow web-based MCP clients to connect directly.

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

// Optional: Optimize Kestrel for HTTP/2 Streams
builder.ConfigureKestrelForMcp();

// Add MCP Server
builder.Services.AddMcpHttpServer(options =>
{
    options.ServerName = "MyMcpServer";
    options.Endpoint = "/mcp"; // The HTTP endpoint MCP clients will hit
    options.RequireAuthorization = false; // Set to true to require auth
    options.UseDefaultCors = true; 
});

var app = builder.Build();

// Map the /mcp endpoint
app.MapMcpHttpServer();

app.Run();
```

### 2. Create Tools, Resources, and Prompts
Simply create a class anywhere in your project and decorate it with `[McpHandler]`. Use the corresponding attributes for your methods:

```csharp
using McpHttpServer.Attributes;

[McpHandler]
public class MyAiTools
{
    [McpTool("calculate_sum", "Calculates the sum of two numbers.")]
    public int CalculateSum([McpParameter] int a, [McpParameter] int b)
    {
        return a + b;
    }

    [McpResource("system://status", "System Status", "Returns the current system status.", "application/json")]
    public string GetStatus()
    {
        return "{ \"status\": \"online\", \"uptime\": \"99.9%\" }";
    }

    [McpPrompt("greet_user", "Generates a greeting prompt.")]
    public string Greet([McpArgument] string username)
    {
        return $"Hello {username}, how can I help you today?";
    }
}
```

That's it! The library will auto-discover `MyAiTools` on startup, build the required JSON schemas, and expose them over the Streamable HTTP transport.

## Testing with MCP Inspector

You can easily test your server using the official MCP Inspector.
1. Run your .NET application.
2. Launch the inspector: `npx @modelcontextprotocol/inspector`
3. Select **SSE** transport and connect to `http://localhost:<port>/mcp`.
