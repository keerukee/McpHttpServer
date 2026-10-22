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
    public string? Title { get; set; }
    public string? IconSrc { get; set; }
    public string? IconMimeType { get; set; }

    public McpResourceAttribute(string uri, string name, string description, string mimeType = "text/plain")
    {
        Uri = uri;
        Name = name;
        Description = description;
        MimeType = mimeType;
    }
}

/// <summary>
/// Marks a method as a parameterized MCP Resource Template (e.g. "users://{id}/profile") exposed via resources/templates/list.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class McpResourceTemplateAttribute : Attribute
{
    public string UriTemplate { get; }
    public string Name { get; }
    public string Description { get; }
    public string MimeType { get; }
    public string? Title { get; set; }
    public string? IconSrc { get; set; }
    public string? IconMimeType { get; set; }

    public McpResourceTemplateAttribute(string uriTemplate, string name, string description, string mimeType = "text/plain")
    {
        UriTemplate = uriTemplate;
        Name = name;
        Description = description;
        MimeType = mimeType;
    }
}
