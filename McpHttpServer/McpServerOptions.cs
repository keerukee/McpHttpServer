namespace McpHttpServer;

/// <summary>
/// Configuration options for the MCP HTTP Server (Streamable HTTP).
/// Supports MCP specifications 2024-11-05, 2025-03-26, 2025-06-18, 2025-11-25, and 2026-07-28.
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
    public string ServerVersion { get; set; } = "1.1.1";

    /// <summary>
    /// Optional human-readable title of the MCP server implementation (MCP SEP-973).
    /// </summary>
    public string? ServerTitle { get; set; }

    /// <summary>
    /// Optional website URL of the MCP server implementation (MCP SEP-973).
    /// </summary>
    public string? WebsiteUrl { get; set; }

    /// <summary>
    /// Optional icons representing this MCP server implementation (MCP SEP-973).
    /// Relative paths like "/icon.png" are automatically resolved to absolute URLs using the incoming HTTP request host.
    /// </summary>
    public List<McpIcon> Icons { get; set; } = new();

    /// <summary>
    /// The default MCP protocol version to advertise when the client does not request a specific supported version.
    /// Defaults to "2025-11-25" for out-of-the-box compatibility with Devin Local, Claude, and MCP Inspector,
    /// while "2026-07-28" is negotiated automatically when requested.
    /// </summary>
    public string ProtocolVersion { get; set; } = "2025-11-25";

    /// <summary>
    /// List of protocol versions supported by this server during version negotiation and header validation.
    /// </summary>
    public HashSet<string> SupportedProtocolVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "2026-07-28",
        "2025-11-25",
        "2025-06-18",
        "2025-03-26",
        "2024-11-05"
    };

    /// <summary>
    /// Optional instructions describing how to use this server and its features, returned during initialization (MCP 2025-03-26+).
    /// </summary>
    public string? Instructions { get; set; }

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

    /// <summary>
    /// When true, operates in pure stateless mode (MCP 2026-07-28): no Mcp-Session-Id headers are generated on initialize.
    /// Even when false (default), requests without a session ID are processed statelessly.
    /// </summary>
    public bool StatelessMode { get; set; } = false;

    /// <summary>
    /// Validates the incoming MCP-Protocol-Version HTTP header (if provided by the client) against SupportedProtocolVersions.
    /// Returns HTTP 400 Bad Request if an unsupported version is specified (MCP 2025-06-18+).
    /// </summary>
    public bool ValidateProtocolVersionHeader { get; set; } = true;

    /// <summary>
    /// Validates incoming Mcp-Method and Mcp-Name HTTP headers (if provided by the client) against the JSON-RPC payload.
    /// Returns HTTP 400 Bad Request on mismatch to prevent gateway spoofing (MCP 2026-07-28 / SEP-2243).
    /// </summary>
    public bool ValidateRoutingHeaders { get; set; } = true;

    /// <summary>
    /// Optional freshness Time-To-Live (in milliseconds) advertised on list and discover responses (tools/list, resources/list, prompts/list, server/discover)
    /// for client and gateway caching (MCP 2026-07-28).
    /// </summary>
    public int? ListCacheTtlMs { get; set; }

    /// <summary>
    /// Cache scope advertised on cacheable responses ("public" or "private", MCP 2026-07-28).
    /// </summary>
    public string CacheScope { get; set; } = "public";

    /// <summary>
    /// Enables the io.modelcontextprotocol/tasks extension (tasks/get, tasks/list, tasks/update, tasks/cancel, MCP 2026-07-28 / SEP-2663).
    /// </summary>
    public bool EnableTasksExtension { get; set; } = true;

    /// <summary>
    /// Optional set of allowed Origin header values (e.g., "http://localhost:5173", "https://app.devin.ai").
    /// When non-empty, requests containing an Origin header not in this set are rejected with HTTP 403 Forbidden (MCP 2025-11-25+).
    /// </summary>
    public HashSet<string> AllowedOrigins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional OAuth 2.0 Protected Resource Metadata (RFC 9728) exposed at /.well-known/oauth-protected-resource
    /// for automatic OAuth discovery by MCP clients such as Devin, Claude, and MCP Inspector.
    /// </summary>
    public McpOAuthProtectedResourceOptions? OAuthProtectedResource { get; set; }
}

/// <summary>
/// Options for RFC 9728 OAuth 2.0 Protected Resource Metadata discovery endpoint (/.well-known/oauth-protected-resource).
/// </summary>
public class McpOAuthProtectedResourceOptions
{
    /// <summary>
    /// The resource identifier URL (defaults to the server's base URL + MCP endpoint if null).
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>
    /// List of OAuth 2.0 / OIDC authorization server issuer URLs.
    /// </summary>
    public List<string> AuthorizationServers { get; set; } = new();

    /// <summary>
    /// List of OAuth 2.0 scopes supported by this MCP resource server.
    /// </summary>
    public List<string> ScopesSupported { get; set; } = new();

    /// <summary>
    /// Supported methods for sending OAuth 2.0 Bearer tokens (default: ["header"]).
    /// </summary>
    public List<string> BearerMethodsSupported { get; set; } = new() { "header" };

    /// <summary>
    /// Whether OAuth Client ID Metadata Documents are supported for client registration (MCP SEP-991).
    /// </summary>
    public bool ClientIdMetadataDocumentSupported { get; set; } = true;
}
