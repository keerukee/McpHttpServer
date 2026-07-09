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
