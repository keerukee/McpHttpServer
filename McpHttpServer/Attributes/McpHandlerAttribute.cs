namespace McpHttpServer.Attributes;

/// <summary>
/// Marks a class as an MCP handler that will be auto-discovered and registered.
/// Apply this to classes containing [McpTool], [McpResource], or [McpPrompt] methods.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class McpHandlerAttribute : Attribute
{
}
