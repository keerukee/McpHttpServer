namespace McpHttpServer;

/// <summary>
/// Custom exception for MCP protocol errors.
/// </summary>
public class McpException : Exception
{
    public int Code { get; }

    public McpException(int code, string message) : base(message)
    {
        Code = code;
    }

    // Standard JSON-RPC error codes
    public static McpException ParseError(string message) => new(-32700, message);
    public static McpException InvalidRequest(string message) => new(-32600, message);
    public static McpException MethodNotFound(string method) => new(-32601, $"Method not found: {method}");
    public static McpException InvalidParams(string message) => new(-32602, message);
    public static McpException InternalError(string message) => new(-32603, message);
    public static McpUnauthorizedException Unauthorized(string message) => new(message);
}

/// <summary>
/// Exception thrown when an application authorization callback denies access to an MCP Tool, Resource, or Prompt.
/// </summary>
public class McpUnauthorizedException : McpException
{
    public McpUnauthorizedException(string message) : base(-32001, message)
    {
    }
}
