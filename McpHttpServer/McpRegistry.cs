using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using McpHttpServer.Attributes;

namespace McpHttpServer;

/// <summary>
/// Registry that auto-discovers and manages MCP Tools, Resources, and Prompts.
/// </summary>
public class McpRegistry
{
    private readonly Dictionary<string, ToolRegistration> _tools = new();
    private readonly Dictionary<string, ResourceRegistration> _resources = new();
    private readonly Dictionary<string, PromptRegistration> _prompts = new();
    private readonly IServiceProvider _serviceProvider;

    public IReadOnlyDictionary<string, ToolRegistration> Tools => _tools;
    public IReadOnlyDictionary<string, ResourceRegistration> Resources => _resources;
    public IReadOnlyDictionary<string, PromptRegistration> Prompts => _prompts;

    public McpRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Scans and registers all decorated methods from the given types.
    /// </summary>
    public void RegisterFromTypes(params Type[] types)
    {
        foreach (var type in types)
        {
            var instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
            RegisterHandlers(type, instance);
        }
    }

    /// <summary>
    /// Scans and registers all decorated methods from the given instances.
    /// </summary>
    public void RegisterFromInstances(params object[] instances)
    {
        foreach (var instance in instances)
        {
            RegisterHandlers(instance.GetType(), instance);
        }
    }

    /// <summary>
    /// Scans all assemblies for classes with MCP handler attributes.
    /// </summary>
    public void RegisterFromAssemblies(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && HasMcpAttributes(t));

            foreach (var type in types)
            {
                var instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
                RegisterHandlers(type, instance);
            }
        }
    }

    private static bool HasMcpAttributes(Type type)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Any(m => m.GetCustomAttribute<McpToolAttribute>() != null ||
                      m.GetCustomAttribute<McpResourceAttribute>() != null ||
                      m.GetCustomAttribute<McpPromptAttribute>() != null);
    }

    private void RegisterHandlers(Type type, object? instance)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        foreach (var method in methods)
        {
            RegisterTool(method, instance);
            RegisterResource(method, instance);
            RegisterPrompt(method, instance);
        }
    }

    private void RegisterTool(MethodInfo method, object? instance)
    {
        var toolAttr = method.GetCustomAttribute<McpToolAttribute>();
        if (toolAttr == null) return;

        var parameters = method.GetParameters()
            .Select(p => new ParameterRegistration
            {
                Name = p.Name ?? "param",
                Type = GetJsonType(p.ParameterType),
                ClrType = p.ParameterType,
                Description = p.GetCustomAttribute<McpParameterAttribute>()?.Description ?? $"Parameter: {p.Name}",
                Required = !p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) == null,
                EnumValues = p.GetCustomAttribute<McpParameterAttribute>()?.EnumValues,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
            })
            .ToList();

        _tools[toolAttr.Name] = new ToolRegistration
        {
            Name = toolAttr.Name,
            Description = toolAttr.Description,
            Parameters = parameters,
            Method = method,
            Instance = method.IsStatic ? null : instance
        };

        Console.WriteLine($"[MCP] Registered tool: {toolAttr.Name}");
    }

    private void RegisterResource(MethodInfo method, object? instance)
    {
        var resourceAttr = method.GetCustomAttribute<McpResourceAttribute>();
        if (resourceAttr == null) return;

        _resources[resourceAttr.Uri] = new ResourceRegistration
        {
            Uri = resourceAttr.Uri,
            Name = resourceAttr.Name,
            Description = resourceAttr.Description,
            MimeType = resourceAttr.MimeType,
            Method = method,
            Instance = method.IsStatic ? null : instance
        };

        Console.WriteLine($"[MCP] Registered resource: {resourceAttr.Uri}");
    }

    private void RegisterPrompt(MethodInfo method, object? instance)
    {
        var promptAttr = method.GetCustomAttribute<McpPromptAttribute>();
        if (promptAttr == null) return;

        var arguments = method.GetParameters()
            .Select(p => new ArgumentRegistration
            {
                Name = p.Name ?? "arg",
                ClrType = p.ParameterType,
                Description = p.GetCustomAttribute<McpArgumentAttribute>()?.Description ?? $"Argument: {p.Name}",
                Required = !p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) == null,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
            })
            .ToList();

        _prompts[promptAttr.Name] = new PromptRegistration
        {
            Name = promptAttr.Name,
            Description = promptAttr.Description,
            Arguments = arguments,
            Method = method,
            Instance = method.IsStatic ? null : instance
        };

        Console.WriteLine($"[MCP] Registered prompt: {promptAttr.Name}");
    }

    /// <summary>
    /// Invokes a registered tool by name with JSON arguments.
    /// </summary>
    public string InvokeTool(string name, JsonElement arguments)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw McpException.InvalidParams($"Unknown tool: {name}");

        try
        {
            var args = BuildMethodArguments(tool.Parameters, arguments);
            var result = tool.Method.Invoke(tool.Instance, args);
            return result?.ToString() ?? "";
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>
    /// Reads a registered resource by URI.
    /// </summary>
    public (string content, string mimeType) ReadResource(string uri)
    {
        if (!_resources.TryGetValue(uri, out var resource))
            throw McpException.InvalidParams($"Resource not found: {uri}");

        try
        {
            var result = resource.Method.Invoke(resource.Instance, null);
            return (result?.ToString() ?? "", resource.MimeType);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>
    /// Gets a registered prompt by name with JSON arguments.
    /// </summary>
    public string GetPrompt(string name, JsonElement arguments)
    {
        if (!_prompts.TryGetValue(name, out var prompt))
            throw McpException.InvalidParams($"Unknown prompt: {name}");

        try
        {
            var args = BuildPromptArguments(prompt.Arguments, arguments);
            var result = prompt.Method.Invoke(prompt.Instance, args);
            return result?.ToString() ?? "";
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static object?[] BuildMethodArguments(List<ParameterRegistration> parameters, JsonElement arguments)
    {
        var args = new object?[parameters.Count];

        for (int i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            
            if (arguments.ValueKind != JsonValueKind.Undefined && 
                arguments.TryGetProperty(param.Name, out var value))
            {
                args[i] = ConvertJsonValue(value, param.ClrType);
            }
            else if (!param.Required)
            {
                args[i] = param.DefaultValue;
            }
            else
            {
                throw McpException.InvalidParams($"Required parameter '{param.Name}' is missing");
            }
        }

        return args;
    }

    private static object?[] BuildPromptArguments(List<ArgumentRegistration> arguments, JsonElement jsonArgs)
    {
        var args = new object?[arguments.Count];

        for (int i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            
            if (jsonArgs.ValueKind != JsonValueKind.Undefined && 
                jsonArgs.TryGetProperty(arg.Name, out var value))
            {
                args[i] = ConvertJsonValue(value, arg.ClrType);
            }
            else if (!arg.Required)
            {
                args[i] = arg.DefaultValue;
            }
            else
            {
                throw McpException.InvalidParams($"Required argument '{arg.Name}' is missing");
            }
        }

        return args;
    }

    private static object? ConvertJsonValue(JsonElement value, Type targetType)
    {
        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value.ValueKind == JsonValueKind.Null)
            return null;

        return underlyingType switch
        {
            Type t when t == typeof(string) => value.GetString(),
            Type t when t == typeof(int) => value.GetInt32(),
            Type t when t == typeof(long) => value.GetInt64(),
            Type t when t == typeof(double) => value.GetDouble(),
            Type t when t == typeof(float) => value.GetSingle(),
            Type t when t == typeof(decimal) => value.GetDecimal(),
            Type t when t == typeof(bool) => value.GetBoolean(),
            Type t when t == typeof(DateTime) => value.GetDateTime(),
            Type t when t == typeof(Guid) => value.GetGuid(),
            Type t when t.IsEnum => Enum.Parse(underlyingType, value.GetString() ?? ""),
            _ => JsonSerializer.Deserialize(value.GetRawText(), targetType)
        };
    }

    private static string GetJsonType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        return underlyingType switch
        {
            Type t when t == typeof(string) => "string",
            Type t when t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) => "integer",
            Type t when t == typeof(double) || t == typeof(float) || t == typeof(decimal) => "number",
            Type t when t == typeof(bool) => "boolean",
            Type t when t.IsEnum => "string",
            Type t when t.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && t != typeof(string) => "array",
            _ => "object"
        };
    }

    /// <summary>
    /// Generates the tools/list response.
    /// </summary>
    public object GetToolsList()
    {
        var tools = _tools.Values.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = new
            {
                type = "object",
                properties = t.Parameters.ToDictionary(
                    p => p.Name,
                    p => CreatePropertySchema(p)
                ),
                required = t.Parameters.Where(p => p.Required).Select(p => p.Name).ToArray()
            }
        }).ToArray();

        return new { tools };
    }

    /// <summary>
    /// Generates the resources/list response.
    /// </summary>
    public object GetResourcesList()
    {
        var resources = _resources.Values.Select(r => new
        {
            uri = r.Uri,
            name = r.Name,
            description = r.Description,
            mimeType = r.MimeType
        }).ToArray();

        return new { resources };
    }

    /// <summary>
    /// Generates the prompts/list response.
    /// </summary>
    public object GetPromptsList()
    {
        var prompts = _prompts.Values.Select(p => new
        {
            name = p.Name,
            description = p.Description,
            arguments = p.Arguments.Select(a => new
            {
                name = a.Name,
                description = a.Description,
                required = a.Required
            }).ToArray()
        }).ToArray();

        return new { prompts };
    }

    private static object CreatePropertySchema(ParameterRegistration param)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = param.Type,
            ["description"] = param.Description
        };

        if (param.EnumValues != null && param.EnumValues.Length > 0)
        {
            schema["enum"] = param.EnumValues;
        }
        else if (param.ClrType.IsEnum)
        {
            schema["enum"] = Enum.GetNames(param.ClrType);
        }

        return schema;
    }
}

public class ToolRegistration
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required List<ParameterRegistration> Parameters { get; init; }
    public required MethodInfo Method { get; init; }
    public object? Instance { get; init; }
}

public class ParameterRegistration
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required Type ClrType { get; init; }
    public required string Description { get; init; }
    public required bool Required { get; init; }
    public string[]? EnumValues { get; init; }
    public object? DefaultValue { get; init; }
}

public class ResourceRegistration
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string MimeType { get; init; }
    public required MethodInfo Method { get; init; }
    public object? Instance { get; init; }
}

public class PromptRegistration
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required List<ArgumentRegistration> Arguments { get; init; }
    public required MethodInfo Method { get; init; }
    public object? Instance { get; init; }
}

public class ArgumentRegistration
{
    public required string Name { get; init; }
    public required Type ClrType { get; init; }
    public required string Description { get; init; }
    public required bool Required { get; init; }
    public object? DefaultValue { get; init; }
}
