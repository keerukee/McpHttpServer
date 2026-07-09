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
}
