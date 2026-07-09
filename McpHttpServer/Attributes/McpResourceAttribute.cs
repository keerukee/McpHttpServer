namespace McpHttpServer.Attributes;

/// <summary>
/// Marks a method as an MCP Resource that will be auto-discovered and registered.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class McpResourceAttribute : Attribute
{
    public string Uri { get; }
    public string Name { get; }
    public string Description { get; }
    public string MimeType { get; }

    public McpResourceAttribute(string uri, string name, string description, string mimeType = "text/plain")
    {
        Uri = uri;
        Name = name;
        Description = description;
        MimeType = mimeType;
    }
}
