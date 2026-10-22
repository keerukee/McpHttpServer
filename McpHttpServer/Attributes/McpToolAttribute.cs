namespace McpHttpServer.Attributes;

/// <summary>
/// Marks a method as an MCP Tool that will be auto-discovered and registered.
/// Parameters are automatically inferred from method signature.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class McpToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }

    /// <summary>
    /// Optional human-readable title for the tool (MCP 2025-06-18+).
    /// </summary>
    public string? Title { get; set; }

    private bool? _readOnlyHint;
    private bool? _destructiveHint;
    private bool? _idempotentHint;
    private bool? _openWorldHint;

    /// <summary>
    /// If true, the tool does not modify its environment (MCP ToolAnnotations).
    /// </summary>
    public bool ReadOnlyHint
    {
        get => _readOnlyHint ?? false;
        set => _readOnlyHint = value;
    }

    /// <summary>
    /// If true, the tool may perform destructive updates to its environment (MCP ToolAnnotations).
    /// </summary>
    public bool DestructiveHint
    {
        get => _destructiveHint ?? true;
        set => _destructiveHint = value;
    }

    /// <summary>
    /// If true, calling the tool repeatedly with the same arguments has no additional effect (MCP ToolAnnotations).
    /// </summary>
    public bool IdempotentHint
    {
        get => _idempotentHint ?? false;
        set => _idempotentHint = value;
    }

    /// <summary>
    /// If true, this tool may interact with an "open world" of external entities (MCP ToolAnnotations).
    /// </summary>
    public bool OpenWorldHint
    {
        get => _openWorldHint ?? true;
        set => _openWorldHint = value;
    }

    /// <summary>
    /// If true, generates an outputSchema for the method's return type and returns structuredContent (MCP 2025-06-18+).
    /// </summary>
    public bool UseStructuredOutput { get; set; }

    /// <summary>
    /// Optional icon URI (HTTP/HTTPS URL or data: URI) for this tool (MCP SEP-973).
    /// </summary>
    public string? IconSrc { get; set; }

    /// <summary>
    /// Optional icon MIME type (e.g., "image/png", "image/svg+xml") when IconSrc is set (MCP SEP-973).
    /// </summary>
    public string? IconMimeType { get; set; }

    internal bool HasAnnotations =>
        _readOnlyHint.HasValue || _destructiveHint.HasValue || _idempotentHint.HasValue || _openWorldHint.HasValue || !string.IsNullOrEmpty(Title);

    internal bool? RawReadOnlyHint => _readOnlyHint;
    internal bool? RawDestructiveHint => _destructiveHint;
    internal bool? RawIdempotentHint => _idempotentHint;
    internal bool? RawOpenWorldHint => _openWorldHint;

    public McpToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }
}

/// <summary>
/// Optional attribute to provide additional metadata for a tool parameter.
/// If not specified, parameter info is inferred from the method signature.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public class McpParameterAttribute : Attribute
{
    public string? Description { get; set; }
    public string[]? EnumValues { get; set; }

    public McpParameterAttribute(string? description = null)
    {
        Description = description;
    }
}

/// <summary>
/// Attaches one or more custom icons to an MCP tool, resource, resource template, or prompt (MCP SEP-973).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class McpIconAttribute : Attribute
{
    public string Src { get; }
    public string? MimeType { get; set; }
    public string[]? Sizes { get; set; }
    public string? Theme { get; set; }

    public McpIconAttribute(string src, string? mimeType = null, string? theme = null, params string[] sizes)
    {
        Src = src;
        MimeType = mimeType;
        Theme = theme;
        Sizes = sizes.Length > 0 ? sizes : null;
    }
}
