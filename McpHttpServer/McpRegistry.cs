using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using McpHttpServer.Attributes;

namespace McpHttpServer;

/// <summary>
/// Registry that auto-discovers and manages MCP Tools, Resources, Resource Templates, and Prompts.
/// </summary>
public class McpRegistry
{
    private readonly Dictionary<string, ToolRegistration> _tools = new();
    private readonly Dictionary<string, ResourceRegistration> _resources = new();
    private readonly Dictionary<string, ResourceTemplateRegistration> _resourceTemplates = new();
    private readonly Dictionary<string, PromptRegistration> _prompts = new();
    private readonly IServiceProvider _serviceProvider;

    public IReadOnlyDictionary<string, ToolRegistration> Tools => _tools;
    public IReadOnlyDictionary<string, ResourceRegistration> Resources => _resources;
    public IReadOnlyDictionary<string, ResourceTemplateRegistration> ResourceTemplates => _resourceTemplates;
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
        if (type.GetCustomAttribute<McpHandlerAttribute>() != null)
            return true;

        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Any(m => m.GetCustomAttribute<McpToolAttribute>() != null ||
                      m.GetCustomAttribute<McpResourceAttribute>() != null ||
                      m.GetCustomAttribute<McpResourceTemplateAttribute>() != null ||
                      m.GetCustomAttribute<McpPromptAttribute>() != null);
    }

    private void RegisterHandlers(Type type, object? instance)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        foreach (var method in methods)
        {
            RegisterTool(method, instance);
            RegisterResource(method, instance);
            RegisterResourceTemplate(method, instance);
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
                IsCancellationToken = p.ParameterType == typeof(CancellationToken),
                Description = p.GetCustomAttribute<McpParameterAttribute>()?.Description ?? $"Parameter: {p.Name}",
                Required = p.ParameterType != typeof(CancellationToken) && !p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) == null,
                EnumValues = p.GetCustomAttribute<McpParameterAttribute>()?.EnumValues,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
            })
            .ToList();

        Dictionary<string, object>? annotations = null;
        if (toolAttr.HasAnnotations)
        {
            annotations = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(toolAttr.Title)) annotations["title"] = toolAttr.Title!;
            if (toolAttr.RawReadOnlyHint.HasValue) annotations["readOnlyHint"] = toolAttr.RawReadOnlyHint.Value;
            if (toolAttr.RawDestructiveHint.HasValue) annotations["destructiveHint"] = toolAttr.RawDestructiveHint.Value;
            if (toolAttr.RawIdempotentHint.HasValue) annotations["idempotentHint"] = toolAttr.RawIdempotentHint.Value;
            if (toolAttr.RawOpenWorldHint.HasValue) annotations["openWorldHint"] = toolAttr.RawOpenWorldHint.Value;
        }

        var unwrappedReturnType = UnwrapAsyncReturnType(method.ReturnType);
        object? outputSchema = null;
        if (toolAttr.UseStructuredOutput && IsStructuredObjectType(unwrappedReturnType))
        {
            outputSchema = CreateTypeSchema(unwrappedReturnType);
        }

        var icons = ExtractIcons(method, toolAttr.IconSrc, toolAttr.IconMimeType);

        _tools[toolAttr.Name] = new ToolRegistration
        {
            Name = toolAttr.Name,
            Title = toolAttr.Title,
            Description = toolAttr.Description,
            Parameters = parameters,
            Method = method,
            Instance = method.IsStatic ? null : instance,
            Annotations = annotations,
            OutputSchema = outputSchema,
            UseStructuredOutput = toolAttr.UseStructuredOutput,
            Icons = icons
        };

        Console.WriteLine($"[MCP] Registered tool: {toolAttr.Name}");
    }

    private static List<McpIcon>? ExtractIcons(MethodInfo method, string? primaryIconSrc, string? primaryIconMimeType)
    {
        var icons = new List<McpIcon>();
        if (!string.IsNullOrEmpty(primaryIconSrc))
        {
            icons.Add(new McpIcon { Src = primaryIconSrc!, MimeType = primaryIconMimeType });
        }

        foreach (var iconAttr in method.GetCustomAttributes<McpIconAttribute>())
        {
            icons.Add(new McpIcon
            {
                Src = iconAttr.Src,
                MimeType = iconAttr.MimeType,
                Theme = iconAttr.Theme,
                Sizes = iconAttr.Sizes
            });
        }

        return icons.Count > 0 ? icons : null;
    }

    private void RegisterResource(MethodInfo method, object? instance)
    {
        var resourceAttr = method.GetCustomAttribute<McpResourceAttribute>();
        if (resourceAttr == null) return;

        // If the URI contains template parameters like {id}, also register as a resource template
        if (resourceAttr.Uri.Contains('{') && resourceAttr.Uri.Contains('}'))
        {
            RegisterResourceTemplateInternal(
                resourceAttr.Uri,
                resourceAttr.Name,
                resourceAttr.Title,
                resourceAttr.Description,
                resourceAttr.MimeType,
                resourceAttr.IconSrc,
                resourceAttr.IconMimeType,
                method,
                instance);
            return;
        }

        _resources[resourceAttr.Uri] = new ResourceRegistration
        {
            Uri = resourceAttr.Uri,
            Name = resourceAttr.Name,
            Title = resourceAttr.Title,
            Description = resourceAttr.Description,
            MimeType = resourceAttr.MimeType,
            Icons = ExtractIcons(method, resourceAttr.IconSrc, resourceAttr.IconMimeType),
            Method = method,
            Instance = method.IsStatic ? null : instance
        };

        Console.WriteLine($"[MCP] Registered resource: {resourceAttr.Uri}");
    }

    private void RegisterResourceTemplate(MethodInfo method, object? instance)
    {
        var templateAttr = method.GetCustomAttribute<McpResourceTemplateAttribute>();
        if (templateAttr == null) return;

        RegisterResourceTemplateInternal(
            templateAttr.UriTemplate,
            templateAttr.Name,
            templateAttr.Title,
            templateAttr.Description,
            templateAttr.MimeType,
            templateAttr.IconSrc,
            templateAttr.IconMimeType,
            method,
            instance);
    }

    private void RegisterResourceTemplateInternal(
        string uriTemplate,
        string name,
        string? title,
        string description,
        string mimeType,
        string? iconSrc,
        string? iconMimeType,
        MethodInfo method,
        object? instance)
    {
        var parameters = method.GetParameters()
            .Select(p => new ParameterRegistration
            {
                Name = p.Name ?? "param",
                Type = GetJsonType(p.ParameterType),
                ClrType = p.ParameterType,
                IsCancellationToken = p.ParameterType == typeof(CancellationToken),
                Description = p.GetCustomAttribute<McpParameterAttribute>()?.Description ?? $"Parameter: {p.Name}",
                Required = p.ParameterType != typeof(CancellationToken) && !p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) == null,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
            })
            .ToList();

        _resourceTemplates[uriTemplate] = new ResourceTemplateRegistration
        {
            UriTemplate = uriTemplate,
            Name = name,
            Title = title,
            Description = description,
            MimeType = mimeType,
            Icons = ExtractIcons(method, iconSrc, iconMimeType),
            Parameters = parameters,
            RegexPattern = BuildUriTemplateRegex(uriTemplate),
            Method = method,
            Instance = method.IsStatic ? null : instance
        };

        Console.WriteLine($"[MCP] Registered resource template: {uriTemplate}");
    }

    private static Regex BuildUriTemplateRegex(string uriTemplate)
    {
        // Convert e.g. "users://{id}/profile" into "^users://(?<id>[^/]+)/profile$"
        var pattern = "^" + Regex.Replace(Regex.Escape(uriTemplate), @"\\\{(\w+)\}", "(?<$1>[^/]+)") + "$";
        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
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
                IsCancellationToken = p.ParameterType == typeof(CancellationToken),
                Description = p.GetCustomAttribute<McpArgumentAttribute>()?.Description ?? $"Argument: {p.Name}",
                Required = p.ParameterType != typeof(CancellationToken) && !p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) == null,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null
            })
            .ToList();

        _prompts[promptAttr.Name] = new PromptRegistration
        {
            Name = promptAttr.Name,
            Title = promptAttr.Title,
            Description = promptAttr.Description,
            Icons = ExtractIcons(method, promptAttr.IconSrc, promptAttr.IconMimeType),
            Arguments = arguments,
            Method = method,
            Instance = method.IsStatic ? null : instance
        };

        Console.WriteLine($"[MCP] Registered prompt: {promptAttr.Name}");
    }

    /// <summary>
    /// Invokes a registered tool by name with JSON arguments and returns the string representation.
    /// </summary>
    public string InvokeTool(string name, JsonElement arguments)
    {
        return InvokeToolAsync(name, arguments).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously invokes a registered tool by name with JSON arguments and returns the string representation.
    /// </summary>
    public async Task<string> InvokeToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var rawResult = await InvokeToolRawAsync(name, arguments, cancellationToken);
        if (rawResult == null) return "";
        if (rawResult is string str) return str;
        if (IsStructuredObjectType(rawResult.GetType()))
            return JsonSerializer.Serialize(rawResult);
        return rawResult.ToString() ?? "";
    }

    /// <summary>
    /// Asynchronously invokes a registered tool and returns the raw CLR return value (supporting McpToolResult, McpContent, POCOs, and primitives).
    /// </summary>
    public async Task<object?> InvokeToolRawAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw McpException.InvalidParams($"Unknown tool: {name}");

        try
        {
            var args = BuildMethodArguments(tool.Parameters, arguments, cancellationToken);
            var raw = tool.Method.Invoke(tool.Instance, args);
            return await UnwrapAsyncResult(raw);
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
        return ReadResourceAsync(uri).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously reads a registered resource or dynamic resource template by URI.
    /// </summary>
    public async Task<(string content, string mimeType)> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (_resources.TryGetValue(uri, out var resource))
        {
            try
            {
                var parameters = resource.Method.GetParameters();
                object?[]? invokeArgs = null;
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(CancellationToken))
                {
                    invokeArgs = new object?[] { cancellationToken };
                }

                var raw = resource.Method.Invoke(resource.Instance, invokeArgs);
                var result = await UnwrapAsyncResult(raw);
                var text = result is string s ? s : (result != null && IsStructuredObjectType(result.GetType()) ? JsonSerializer.Serialize(result) : result?.ToString() ?? "");
                return (text, resource.MimeType);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        // Check resource templates
        foreach (var template in _resourceTemplates.Values)
        {
            var match = template.RegexPattern.Match(uri);
            if (match.Success)
            {
                try
                {
                    var args = new object?[template.Parameters.Count];
                    for (int i = 0; i < template.Parameters.Count; i++)
                    {
                        var param = template.Parameters[i];
                        if (param.IsCancellationToken)
                        {
                            args[i] = cancellationToken;
                        }
                        else if (match.Groups[param.Name].Success)
                        {
                            var rawVal = Uri.UnescapeDataString(match.Groups[param.Name].Value);
                            args[i] = ConvertStringValue(rawVal, param.ClrType);
                        }
                        else if (!param.Required)
                        {
                            args[i] = param.DefaultValue;
                        }
                        else
                        {
                            throw McpException.InvalidParams($"Missing URI template parameter '{param.Name}'");
                        }
                    }

                    var raw = template.Method.Invoke(template.Instance, args);
                    var result = await UnwrapAsyncResult(raw);
                    var text = result is string s ? s : (result != null && IsStructuredObjectType(result.GetType()) ? JsonSerializer.Serialize(result) : result?.ToString() ?? "");
                    return (text, template.MimeType);
                }
                catch (TargetInvocationException ex)
                {
                    throw ex.InnerException ?? ex;
                }
            }
        }

        throw McpException.InvalidParams($"Resource not found: {uri}");
    }

    /// <summary>
    /// Gets a registered prompt by name with JSON arguments.
    /// </summary>
    public string GetPrompt(string name, JsonElement arguments)
    {
        return GetPromptAsync(name, arguments).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously gets a registered prompt by name with JSON arguments.
    /// </summary>
    public async Task<string> GetPromptAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!_prompts.TryGetValue(name, out var prompt))
            throw McpException.InvalidParams($"Unknown prompt: {name}");

        try
        {
            var args = BuildPromptArguments(prompt.Arguments, arguments, cancellationToken);
            var raw = prompt.Method.Invoke(prompt.Instance, args);
            var result = await UnwrapAsyncResult(raw);
            return result?.ToString() ?? "";
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static async Task<object?> UnwrapAsyncResult(object? result)
    {
        if (result == null) return null;

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            var taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                var prop = taskType.GetProperty("Result");
                return prop?.GetValue(task);
            }
            return null;
        }

        var type = result.GetType();
        if (type == typeof(ValueTask))
        {
            await ((ValueTask)result).ConfigureAwait(false);
            return null;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTaskMethod = type.GetMethod("AsTask");
            if (asTaskMethod != null && asTaskMethod.Invoke(result, null) is Task valueTaskAsTask)
            {
                await valueTaskAsTask.ConfigureAwait(false);
                return valueTaskAsTask.GetType().GetProperty("Result")?.GetValue(valueTaskAsTask);
            }
        }

        return result;
    }

    internal static Type UnwrapAsyncReturnType(Type returnType)
    {
        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
            return typeof(void);

        if (returnType.IsGenericType)
        {
            var genericDef = returnType.GetGenericTypeDefinition();
            if (genericDef == typeof(Task<>) || genericDef == typeof(ValueTask<>))
            {
                return returnType.GetGenericArguments()[0];
            }
        }

        return returnType;
    }

    internal static bool IsStructuredObjectType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(void) ||
            underlying.IsPrimitive ||
            underlying.IsEnum ||
            underlying == typeof(string) ||
            underlying == typeof(decimal) ||
            underlying == typeof(DateTime) ||
            underlying == typeof(DateTimeOffset) ||
            underlying == typeof(Guid) ||
            underlying == typeof(McpToolResult) ||
            underlying == typeof(McpContent) ||
            typeof(IEnumerable<McpContent>).IsAssignableFrom(underlying))
        {
            return false;
        }

        return true;
    }

    private static object?[] BuildMethodArguments(List<ParameterRegistration> parameters, JsonElement arguments, CancellationToken cancellationToken)
    {
        var args = new object?[parameters.Count];

        for (int i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];

            if (param.IsCancellationToken)
            {
                args[i] = cancellationToken;
            }
            else if (TryGetJsonPropertyCaseInsensitive(arguments, param.Name, out var value))
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

    private static object?[] BuildPromptArguments(List<ArgumentRegistration> arguments, JsonElement jsonArgs, CancellationToken cancellationToken)
    {
        var args = new object?[arguments.Count];

        for (int i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];

            if (arg.IsCancellationToken)
            {
                args[i] = cancellationToken;
            }
            else if (TryGetJsonPropertyCaseInsensitive(jsonArgs, arg.Name, out var value))
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

    private static bool TryGetJsonPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
                return true;

            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static object? ConvertStringValue(string rawValue, Type targetType)
    {
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return underlyingType switch
        {
            Type t when t == typeof(string) => rawValue,
            Type t when t == typeof(int) => int.Parse(rawValue),
            Type t when t == typeof(long) => long.Parse(rawValue),
            Type t when t == typeof(double) => double.Parse(rawValue),
            Type t when t == typeof(float) => float.Parse(rawValue),
            Type t when t == typeof(decimal) => decimal.Parse(rawValue),
            Type t when t == typeof(bool) => bool.Parse(rawValue),
            Type t when t == typeof(Guid) => Guid.Parse(rawValue),
            Type t when t.IsEnum => Enum.Parse(underlyingType, rawValue, ignoreCase: true),
            _ => Convert.ChangeType(rawValue, underlyingType)
        };
    }

    private static object? ConvertJsonValue(JsonElement value, Type targetType)
    {
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind == JsonValueKind.String && underlyingType != typeof(string) && !underlyingType.IsEnum)
        {
            var strVal = value.GetString();
            if (strVal != null)
            {
                return ConvertStringValue(strVal, underlyingType);
            }
        }

        return underlyingType switch
        {
            Type t when t == typeof(string) => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            Type t when t == typeof(int) => value.GetInt32(),
            Type t when t == typeof(long) => value.GetInt64(),
            Type t when t == typeof(double) => value.GetDouble(),
            Type t when t == typeof(float) => value.GetSingle(),
            Type t when t == typeof(decimal) => value.GetDecimal(),
            Type t when t == typeof(bool) => value.GetBoolean(),
            Type t when t == typeof(DateTime) => value.GetDateTime(),
            Type t when t == typeof(Guid) => value.GetGuid(),
            Type t when t.IsEnum => Enum.Parse(underlyingType, value.GetString() ?? "", ignoreCase: true),
            _ => JsonSerializer.Deserialize(value.GetRawText(), targetType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        };
    }

    private static string GetJsonType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        return underlyingType switch
        {
            Type t when t == typeof(string) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(Guid) => "string",
            Type t when t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) => "integer",
            Type t when t == typeof(double) || t == typeof(float) || t == typeof(decimal) => "number",
            Type t when t == typeof(bool) => "boolean",
            Type t when t.IsEnum => "string",
            Type t when t.IsArray || (typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string) && !typeof(IDictionary).IsAssignableFrom(t)) => "array",
            _ => "object"
        };
    }

    /// <summary>
    /// Generates the tools/list response, supporting optional Title, ToolAnnotations (2025-03-26+), outputSchema (2025-06-18+), icons (SEP-973), and ttlMs/cacheScope (2026-07-28).
    /// </summary>
    public object GetToolsList(int? ttlMs = null, string cacheScope = "public")
    {
        var tools = _tools.Values.Select(t =>
        {
            var dict = new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = new
                {
                    type = "object",
                    properties = t.Parameters
                        .Where(p => !p.IsCancellationToken)
                        .ToDictionary(
                            p => p.Name,
                            p => CreatePropertySchema(p)
                        ),
                    required = t.Parameters
                        .Where(p => !p.IsCancellationToken && p.Required)
                        .Select(p => p.Name)
                        .ToArray()
                }
            };

            if (!string.IsNullOrEmpty(t.Title))
                dict["title"] = t.Title!;

            if (t.Annotations != null && t.Annotations.Count > 0)
                dict["annotations"] = t.Annotations;

            if (t.OutputSchema != null)
                dict["outputSchema"] = t.OutputSchema;

            if (t.Icons != null && t.Icons.Count > 0)
                dict["icons"] = t.Icons;

            return dict;
        }).ToArray();

        if (ttlMs.HasValue)
            return new { tools, ttlMs = ttlMs.Value, cacheScope };

        return new { tools };
    }

    /// <summary>
    /// Generates the resources/list response.
    /// </summary>
    public object GetResourcesList(int? ttlMs = null, string cacheScope = "public")
    {
        var resources = _resources.Values.Select(r =>
        {
            var dict = new Dictionary<string, object>
            {
                ["uri"] = r.Uri,
                ["name"] = r.Name,
                ["description"] = r.Description,
                ["mimeType"] = r.MimeType
            };
            if (!string.IsNullOrEmpty(r.Title))
                dict["title"] = r.Title!;
            if (r.Icons != null && r.Icons.Count > 0)
                dict["icons"] = r.Icons;
            return dict;
        }).ToArray();

        if (ttlMs.HasValue)
            return new { resources, ttlMs = ttlMs.Value, cacheScope };

        return new { resources };
    }

    /// <summary>
    /// Generates the resources/templates/list response.
    /// </summary>
    public object GetResourceTemplatesList(int? ttlMs = null, string cacheScope = "public")
    {
        var resourceTemplates = _resourceTemplates.Values.Select(rt =>
        {
            var dict = new Dictionary<string, object>
            {
                ["uriTemplate"] = rt.UriTemplate,
                ["name"] = rt.Name,
                ["description"] = rt.Description,
                ["mimeType"] = rt.MimeType
            };
            if (!string.IsNullOrEmpty(rt.Title))
                dict["title"] = rt.Title!;
            if (rt.Icons != null && rt.Icons.Count > 0)
                dict["icons"] = rt.Icons;
            return dict;
        }).ToArray();

        if (ttlMs.HasValue)
            return new { resourceTemplates, ttlMs = ttlMs.Value, cacheScope };

        return new { resourceTemplates };
    }

    /// <summary>
    /// Generates the prompts/list response.
    /// </summary>
    public object GetPromptsList(int? ttlMs = null, string cacheScope = "public")
    {
        var prompts = _prompts.Values.Select(p =>
        {
            var dict = new Dictionary<string, object>
            {
                ["name"] = p.Name,
                ["description"] = p.Description,
                ["arguments"] = p.Arguments
                    .Where(a => !a.IsCancellationToken)
                    .Select(a => new
                    {
                        name = a.Name,
                        description = a.Description,
                        required = a.Required
                    }).ToArray()
            };
            if (!string.IsNullOrEmpty(p.Title))
                dict["title"] = p.Title!;
            if (p.Icons != null && p.Icons.Count > 0)
                dict["icons"] = p.Icons;
            return dict;
        }).ToArray();

        if (ttlMs.HasValue)
            return new { prompts, ttlMs = ttlMs.Value, cacheScope };

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

    private static object CreateTypeSchema(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var jsonType = GetJsonType(underlying);

        if (jsonType != "object")
        {
            return new { type = jsonType };
        }

        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var prop in underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            var propName = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            properties[propName] = new { type = GetJsonType(prop.PropertyType) };
            if (Nullable.GetUnderlyingType(prop.PropertyType) == null && prop.PropertyType.IsValueType)
            {
                required.Add(propName);
            }
        }

        return new
        {
            type = "object",
            properties,
            required = required.ToArray()
        };
    }
}

public class ToolRegistration
{
    public required string Name { get; init; }
    public string? Title { get; init; }
    public required string Description { get; init; }
    public required List<ParameterRegistration> Parameters { get; init; }
    public required MethodInfo Method { get; init; }
    public object? Instance { get; init; }
    public Dictionary<string, object>? Annotations { get; init; }
    public object? OutputSchema { get; init; }
    public bool UseStructuredOutput { get; init; }
    public List<McpIcon>? Icons { get; init; }
}

public class ParameterRegistration
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required Type ClrType { get; init; }
    public bool IsCancellationToken { get; init; }
    public required string Description { get; init; }
    public required bool Required { get; init; }
    public string[]? EnumValues { get; init; }
    public object? DefaultValue { get; init; }
}

public class ResourceRegistration
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public string? Title { get; init; }
    public required string Description { get; init; }
    public required string MimeType { get; init; }
    public List<McpIcon>? Icons { get; init; }
    public required MethodInfo Method { get; init; }
    public object? Instance { get; init; }
}

public class ResourceTemplateRegistration
{
    public required string UriTemplate { get; init; }
    public required string Name { get; init; }
    public string? Title { get; init; }
    public required string Description { get; init; }
    public required string MimeType { get; init; }
    public List<McpIcon>? Icons { get; init; }
    public required List<ParameterRegistration> Parameters { get; init; }
    public required Regex RegexPattern { get; init; }
    public required MethodInfo Method { get; init; }
    public object? Instance { get; init; }
}

public class PromptRegistration
{
    public required string Name { get; init; }
    public string? Title { get; init; }
    public required string Description { get; init; }
    public List<McpIcon>? Icons { get; init; }
    public required List<ArgumentRegistration> Arguments { get; init; }
    public required MethodInfo Method { get; init; }
    public object? Instance { get; init; }
}

public class ArgumentRegistration
{
    public required string Name { get; init; }
    public required Type ClrType { get; init; }
    public bool IsCancellationToken { get; init; }
    public required string Description { get; init; }
    public required bool Required { get; init; }
    public object? DefaultValue { get; init; }
}
