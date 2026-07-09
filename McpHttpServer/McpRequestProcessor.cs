using System.Text.Json;

namespace McpHttpServer;

/// <summary>
/// Processes MCP JSON-RPC requests using the registry for auto-discovered handlers.
/// </summary>
public class McpRequestProcessor
{
    private readonly McpRegistry _registry;
    private readonly McpServerOptions _options;

    public McpRequestProcessor(McpRegistry registry, McpServerOptions options)
    {
        _registry = registry;
        _options = options;
    }

    public string? Process(string json)
    {
        JsonDocument? doc = null;
        object? requestId = null;

        try
        {
            doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();

            bool hasId = root.TryGetProperty("id", out var idElement);

            // Handle ALL notifications (no response needed)
            if (!hasId || method?.StartsWith("notifications/") == true)
            {
                if (_options.EnableLogging)
                    Console.WriteLine($"[MCP] Notification: {method}");
                return null;
            }

            requestId = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.GetInt64(),
                _ => null
            };

            root.TryGetProperty("params", out var paramsElement);

            object result = method switch
            {
                // Core MCP methods
                "initialize" => HandleInitialize(paramsElement),
                "ping" => new { },

                // Tools
                "tools/list" => _registry.GetToolsList(),
                "tools/call" => HandleToolCall(paramsElement),

                // Resources
                "resources/list" => _registry.GetResourcesList(),
                "resources/read" => HandleResourceRead(paramsElement),
                "resources/templates/list" => new { resourceTemplates = Array.Empty<object>() },

                // Prompts
                "prompts/list" => _registry.GetPromptsList(),
                "prompts/get" => HandlePromptGet(paramsElement),

                // Roots (for filesystem access)
                "roots/list" => new { roots = Array.Empty<object>() },

                // Logging
                "logging/setLevel" => new { },

                // Completion (autocomplete support)
                "completion/complete" => new { completion = new { values = Array.Empty<string>() } },

                _ => throw McpException.MethodNotFound(method ?? "unknown")
            };

            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = requestId, result });
        }
        catch (McpException ex)
        {
            if (_options.EnableLogging)
                Console.WriteLine($"[MCP Error] {ex.Code}: {ex.Message}");
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = requestId, error = new { code = ex.Code, message = ex.Message } });
        }
        catch (Exception ex)
        {
            if (_options.EnableLogging)
                Console.WriteLine($"[MCP Error] {ex.Message}");
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = requestId, error = new { code = -32603, message = ex.Message } });
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private object HandleInitialize(JsonElement paramsElement)
    {
        if (_options.EnableLogging && paramsElement.ValueKind != JsonValueKind.Undefined &&
            paramsElement.TryGetProperty("clientInfo", out var clientInfo))
        {
            var name = clientInfo.TryGetProperty("name", out var n) ? n.GetString() : "Unknown";
            var version = clientInfo.TryGetProperty("version", out var v) ? v.GetString() : "Unknown";
            Console.WriteLine($"[MCP] Client connected: {name} {version}");
        }

        return new
        {
            protocolVersion = _options.ProtocolVersion,
            capabilities = new
            {
                tools = new { listChanged = true },
                resources = new { subscribe = false, listChanged = true },
                prompts = new { listChanged = true }
            },
            serverInfo = new { name = _options.ServerName, version = _options.ServerVersion }
        };
    }

    private object HandleToolCall(JsonElement paramsElement)
    {
        var toolName = paramsElement.GetProperty("name").GetString() ?? "";
        paramsElement.TryGetProperty("arguments", out var args);

        if (_options.EnableLogging)
            Console.WriteLine($"[MCP] Tool call: {toolName}");

        try
        {
            var result = _registry.InvokeTool(toolName, args);
            return new { content = new[] { new { type = "text", text = result } }, isError = false };
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new { content = new[] { new { type = "text", text = $"Error: {ex.Message}" } }, isError = true };
        }
    }

    private object HandleResourceRead(JsonElement paramsElement)
    {
        var uri = paramsElement.GetProperty("uri").GetString() ?? "";
        
        if (_options.EnableLogging)
            Console.WriteLine($"[MCP] Resource read: {uri}");

        var (content, mimeType) = _registry.ReadResource(uri);
        return new { contents = new[] { new { uri, mimeType, text = content } } };
    }

    private object HandlePromptGet(JsonElement paramsElement)
    {
        var name = paramsElement.GetProperty("name").GetString() ?? "";
        paramsElement.TryGetProperty("arguments", out var args);

        if (_options.EnableLogging)
            Console.WriteLine($"[MCP] Prompt get: {name}");

        var text = _registry.GetPrompt(name, args);
        return new
        {
            description = name,
            messages = new[] { new { role = "user", content = new { type = "text", text } } }
        };
    }
}
