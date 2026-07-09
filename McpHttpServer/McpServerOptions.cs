namespace McpHttpServer;

/// <summary>
/// Configuration options for the MCP HTTP Server (Streamable HTTP).
/// </summary>
public class McpServerOptions
{
    /// <summary>
    /// The name of the MCP server (shown to clients).
    /// </summary>
    public string ServerName { get; set; } = "MCP-HTTP-Server";

    /// <summary>
    /// The version of the MCP server.
    /// </summary>
    public string ServerVersion { get; set; } = "1.0.0";

    /// <summary>
    /// The MCP protocol version to advertise.
    /// </summary>
    public string ProtocolVersion { get; set; } = "2024-11-05"; // Or newer if appropriate

    /// <summary>
    /// Enable console logging of MCP requests/responses.
    /// </summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>
    /// Enable detailed HTTP request logging.
    /// </summary>
    public bool EnableHttpLogging { get; set; } = true;

    /// <summary>
    /// The route path for the MCP endpoint.
    /// Default is "/mcp" according to Streamable HTTP conventions.
    /// </summary>
    public string Endpoint { get; set; } = "/mcp";

    /// <summary>
    /// Whether to require authorization for the MCP endpoint.
    /// If true, the endpoint will be protected using ASP.NET Core's [Authorize] mechanism.
    /// </summary>
    public bool RequireAuthorization { get; set; } = false;

    /// <summary>
    /// The name of the authorization policy to use if RequireAuthorization is true.
    /// Leave null to use the default authorization policy.
    /// </summary>
    public string? AuthorizationPolicy { get; set; } = null;

    /// <summary>
    /// Whether to automatically configure a permissive CORS policy (AllowAnyOrigin).
    /// Defaults to false. Set to true if you need the server to be accessible from browser-based clients
    /// and haven't configured CORS globally.
    /// </summary>
    public bool UseDefaultCors { get; set; } = false;
}
