namespace McpHttpServer.Attributes;

/// <summary>
/// Decorates an MCP handler class, tool, resource, resource template, or prompt with an
/// application-defined authorization callback method.
/// If a method or class is not decorated with <see cref="McpAuthorizeAttribute"/>, it is open to all callers.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class McpAuthorizeAttribute : Attribute
{
    /// <summary>
    /// Name of the callback method (sync <c>bool</c>, <c>Task&lt;bool&gt;</c>, or <c>ValueTask&lt;bool&gt;</c>)
    /// that determines whether the current caller is authorized.
    /// </summary>
    public string CallbackMethodName { get; }

    /// <summary>
    /// Optional type declaring the callback method. If null, the callback method is looked up on the MCP handler class itself.
    /// </summary>
    public Type? CallbackType { get; set; }

    /// <summary>
    /// Optional duration in seconds to cache the authorization result per user/session across HTTP requests.
    /// Defaults to 0 (deduplicates multiple calls within the same HTTP request only).
    /// </summary>
    public int CacheDurationSeconds { get; set; }

    public McpAuthorizeAttribute(string callbackMethodName)
    {
        if (string.IsNullOrWhiteSpace(callbackMethodName))
            throw new ArgumentException("Callback method name cannot be null or empty.", nameof(callbackMethodName));

        CallbackMethodName = callbackMethodName;
    }

    public McpAuthorizeAttribute(Type callbackType, string callbackMethodName)
        : this(callbackMethodName)
    {
        CallbackType = callbackType ?? throw new ArgumentNullException(nameof(callbackType));
    }
}
