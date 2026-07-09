namespace McpHttpServer.Attributes;

/// <summary>
/// Marks a method as an MCP Prompt that will be auto-discovered and registered.
/// Parameters are automatically inferred from method signature.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class McpPromptAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }

    public McpPromptAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }
}

/// <summary>
/// Optional attribute to provide additional metadata for a prompt argument.
/// If not specified, argument info is inferred from the method signature.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public class McpArgumentAttribute : Attribute
{
    public string? Description { get; set; }

    public McpArgumentAttribute(string? description = null)
    {
        Description = description;
    }
}
