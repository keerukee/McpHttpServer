using System.Text.Json.Serialization;

namespace McpHttpServer;

/// <summary>
/// Represents a rich content item returned by an MCP tool or prompt (text, image, audio, resource, or resource_link).
/// </summary>
public class McpContent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("resource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Resource { get; init; }

    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Annotations { get; init; }

    /// <summary>
    /// Creates a standard text content block.
    /// </summary>
    public static McpContent CreateText(string text) => new()
    {
        Type = "text",
        Text = text
    };

    /// <summary>
    /// Creates a base64-encoded image content block.
    /// </summary>
    public static McpContent CreateImage(string base64Data, string mimeType = "image/png") => new()
    {
        Type = "image",
        Data = base64Data,
        MimeType = mimeType
    };

    /// <summary>
    /// Creates a base64-encoded audio content block (MCP 2025-03-26+).
    /// </summary>
    public static McpContent CreateAudio(string base64Data, string mimeType = "audio/wav") => new()
    {
        Type = "audio",
        Data = base64Data,
        MimeType = mimeType
    };

    /// <summary>
    /// Creates a resource link content block (MCP 2025-06-18+).
    /// </summary>
    public static McpContent CreateResourceLink(string uri, string? name = null, string? description = null, string? mimeType = null) => new()
    {
        Type = "resource_link",
        Uri = uri,
        Name = name,
        Description = description,
        MimeType = mimeType
    };

    /// <summary>
    /// Creates an embedded resource content block.
    /// </summary>
    public static McpContent CreateEmbeddedResource(string uri, string text, string mimeType = "text/plain") => new()
    {
        Type = "resource",
        Resource = new { uri, mimeType, text }
    };
}

/// <summary>
/// Represents a full tool execution result supporting multiple content blocks, structured content (MCP 2025-06-18+), and error state.
/// </summary>
public class McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpContent> Content { get; set; } = new();

    [JsonPropertyName("structuredContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? StructuredContent { get; set; }

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    public static McpToolResult Success(params McpContent[] content) => new()
    {
        Content = content.ToList(),
        IsError = false
    };

    public static McpToolResult Structured(object structuredData, string? fallbackText = null) => new()
    {
        Content = new List<McpContent>
        {
            McpContent.CreateText(fallbackText ?? System.Text.Json.JsonSerializer.Serialize(structuredData))
        },
        StructuredContent = structuredData,
        IsError = false
    };

    public static McpToolResult Error(string errorMessage) => new()
    {
        Content = new List<McpContent> { McpContent.CreateText(errorMessage) },
        IsError = true
    };
}

/// <summary>
/// Represents an icon metadata descriptor for implementations, tools, resources, and prompts (MCP SEP-973).
/// </summary>
public class McpIcon
{
    /// <summary>
    /// Standard URI pointing to an icon resource (HTTP/HTTPS URL or data: URI).
    /// </summary>
    [JsonPropertyName("src")]
    public required string Src { get; init; }

    /// <summary>
    /// Optional MIME type override (e.g., "image/png", "image/svg+xml").
    /// </summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    /// <summary>
    /// Optional array of sizes in WxH format (e.g., ["48x48", "96x96"] or ["any"]).
    /// </summary>
    [JsonPropertyName("sizes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Sizes { get; init; }

    /// <summary>
    /// Optional theme specifier ("light" or "dark").
    /// </summary>
    [JsonPropertyName("theme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; init; }
}
