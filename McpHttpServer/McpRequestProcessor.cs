using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace McpHttpServer;

/// <summary>
/// Processes MCP JSON-RPC requests using the registry for auto-discovered handlers.
/// Supports MCP specifications 2024-11-05, 2025-03-26, 2025-06-18, 2025-11-25, and 2026-07-28.
/// </summary>
public class McpRequestProcessor
{
    private readonly McpRegistry _registry;
    private readonly McpServerOptions _options;
    private readonly McpTaskStore _taskStore;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public McpTaskStore TaskStore => _taskStore;

    public McpRequestProcessor(
        McpRegistry registry,
        McpServerOptions options,
        McpTaskStore? taskStore = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _registry = registry;
        _options = options;
        _taskStore = taskStore ?? new McpTaskStore();
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Validates the Mcp-Method and Mcp-Name HTTP headers against the JSON-RPC body (MCP 2026-07-28 / SEP-2243).
    /// Returns true if valid (or if headers are not present); returns false with an error message on mismatch.
    /// </summary>
    public bool ValidateRoutingHeaders(string json, string? mcpMethodHeader, string? mcpNameHeader, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrEmpty(mcpMethodHeader) && string.IsNullOrEmpty(mcpNameHeader))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return true;

            if (!string.IsNullOrEmpty(mcpMethodHeader))
            {
                var bodyMethod = root.TryGetProperty("method", out var m) ? m.GetString() : null;
                if (!string.Equals(bodyMethod, mcpMethodHeader, StringComparison.Ordinal))
                {
                    errorMessage = $"Header 'Mcp-Method' ({mcpMethodHeader}) does not match JSON-RPC method ({bodyMethod ?? "null"}).";
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(mcpNameHeader))
            {
                string? bodyName = null;
                if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    if (p.TryGetProperty("name", out var n))
                        bodyName = n.GetString();
                    else if (p.TryGetProperty("uri", out var u))
                        bodyName = u.GetString();
                    else if (p.TryGetProperty("taskId", out var t))
                        bodyName = t.GetString();
                }

                if (!string.Equals(bodyName, mcpNameHeader, StringComparison.Ordinal))
                {
                    errorMessage = $"Header 'Mcp-Name' ({mcpNameHeader}) does not match JSON-RPC target name/uri ({bodyName ?? "null"}).";
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // Let the main processor handle JSON parse errors
            return true;
        }
    }

    /// <summary>
    /// Processes a single or batch JSON-RPC payload synchronously.
    /// </summary>
    public string? Process(string json)
    {
        return ProcessAsync(json).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Processes a single or batch JSON-RPC payload asynchronously.
    /// </summary>
    public async Task<string?> ProcessAsync(string json, CancellationToken cancellationToken = default)
    {
        JsonDocument? doc = null;

        try
        {
            doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Support JSON-RPC batch arrays
            if (root.ValueKind == JsonValueKind.Array)
            {
                var responses = new List<object>();
                foreach (var element in root.EnumerateArray())
                {
                    var responseObj = await ProcessSingleElementAsync(element, cancellationToken);
                    if (responseObj != null)
                    {
                        responses.Add(responseObj);
                    }
                }

                if (responses.Count == 0)
                    return null;

                return JsonSerializer.Serialize(responses);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                var responseObj = await ProcessSingleElementAsync(root, cancellationToken);
                if (responseObj == null)
                    return null;

                return JsonSerializer.Serialize(responseObj);
            }

            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new { code = -32600, message = "Invalid Request" }
            });
        }
        catch (JsonException ex)
        {
            if (_options.EnableLogging)
                Console.WriteLine($"[MCP Parse Error] {ex.Message}");
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = (object?)null,
                error = new { code = -32700, message = $"Parse error: {ex.Message}" }
            });
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private async Task<object?> ProcessSingleElementAsync(JsonElement root, CancellationToken cancellationToken)
    {
        object? requestId = null;

        try
        {
            bool hasId = root.TryGetProperty("id", out var idElement);
            if (hasId)
            {
                requestId = idElement.ValueKind switch
                {
                    JsonValueKind.String => idElement.GetString(),
                    JsonValueKind.Number => idElement.GetInt64(),
                    _ => null
                };
            }

            // If this is a JSON-RPC response sent from the client (e.g. sampling/elicitation response), acknowledge with no body (202 Accepted)
            if (!root.TryGetProperty("method", out var methodElement))
            {
                return null;
            }

            var method = methodElement.GetString();

            // Handle ALL notifications (no response needed -> 202 Accepted)
            if (!hasId || method?.StartsWith("notifications/") == true)
            {
                if (_options.EnableLogging)
                    Console.WriteLine($"[MCP] Notification: {method}");
                return null;
            }

            root.TryGetProperty("params", out var paramsElement);

            // Validate and inspect per-request _meta fields (MCP 2026-07-28 / SEP-2575)
            ValidateRequestMeta(paramsElement, method);

            object result = method switch
            {
                // Core MCP methods (stateful 2024-11-05..2025-11-25 & stateless 2026-07-28)
                "initialize" => HandleInitialize(paramsElement),
                "server/discover" => HandleServerDiscover(paramsElement),
                "ping" => new { },

                // Tools
                "tools/list" => _registry.GetToolsList(_options.ListCacheTtlMs, _options.CacheScope),
                "tools/call" => await HandleToolCallAsync(paramsElement, cancellationToken),

                // Resources
                "resources/list" => _registry.GetResourcesList(_options.ListCacheTtlMs, _options.CacheScope),
                "resources/read" => await HandleResourceReadAsync(paramsElement, cancellationToken),
                "resources/templates/list" => _registry.GetResourceTemplatesList(_options.ListCacheTtlMs, _options.CacheScope),

                // Prompts
                "prompts/list" => _registry.GetPromptsList(_options.ListCacheTtlMs, _options.CacheScope),
                "prompts/get" => await HandlePromptGetAsync(paramsElement, cancellationToken),

                // Tasks extension (io.modelcontextprotocol/tasks - MCP 2026-07-28 / SEP-2663)
                "tasks/get" when _options.EnableTasksExtension => HandleTasksGet(paramsElement),
                "tasks/list" when _options.EnableTasksExtension => HandleTasksList(),
                "tasks/update" when _options.EnableTasksExtension => HandleTasksUpdate(paramsElement),
                "tasks/cancel" when _options.EnableTasksExtension => HandleTasksCancel(paramsElement),

                // Roots (for filesystem access)
                "roots/list" => new { roots = Array.Empty<object>() },

                // Logging
                "logging/setLevel" => new { },

                // Completion (autocomplete support)
                "completion/complete" => new { completion = new { values = Array.Empty<string>(), total = 0, hasMore = false } },

                _ => throw McpException.MethodNotFound(method ?? "unknown")
            };

            return new { jsonrpc = "2.0", id = requestId, result };
        }
        catch (McpException ex)
        {
            if (_options.EnableLogging)
                Console.WriteLine($"[MCP Error] {ex.Code}: {ex.Message}");
            return new { jsonrpc = "2.0", id = requestId, error = new { code = ex.Code, message = ex.Message } };
        }
        catch (Exception ex)
        {
            if (_options.EnableLogging)
                Console.WriteLine($"[MCP Error] {ex.Message}");
            return new { jsonrpc = "2.0", id = requestId, error = new { code = -32603, message = ex.Message } };
        }
    }

    private void ValidateRequestMeta(JsonElement paramsElement, string? method)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object ||
            !paramsElement.TryGetProperty("_meta", out var metaElement) ||
            metaElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (metaElement.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var versionEl))
        {
            var metaVersion = versionEl.GetString();
            // On initialize and server/discover, version negotiation is performed instead of rejecting
            if (!string.IsNullOrEmpty(metaVersion) &&
                method != "initialize" &&
                method != "server/discover" &&
                _options.ValidateProtocolVersionHeader &&
                !_options.SupportedProtocolVersions.Contains(metaVersion))
            {
                throw McpException.InvalidRequest(
                    $"Unsupported protocolVersion '{metaVersion}' in _meta. Supported versions: {string.Join(", ", _options.SupportedProtocolVersions)}");
            }
        }

        if (_options.EnableLogging &&
            metaElement.TryGetProperty("io.modelcontextprotocol/clientInfo", out var clientInfo) &&
            clientInfo.ValueKind == JsonValueKind.Object)
        {
            var name = clientInfo.TryGetProperty("name", out var n) ? n.GetString() : "Unknown";
            var version = clientInfo.TryGetProperty("version", out var v) ? v.GetString() : "Unknown";
            Console.WriteLine($"[MCP _meta] Client: {name} {version}");
        }
    }

    private Dictionary<string, object> BuildServerInfo()
    {
        var info = new Dictionary<string, object>
        {
            ["name"] = _options.ServerName,
            ["version"] = _options.ServerVersion
        };

        if (!string.IsNullOrEmpty(_options.ServerTitle))
            info["title"] = _options.ServerTitle!;

        if (!string.IsNullOrEmpty(_options.WebsiteUrl))
            info["websiteUrl"] = _options.WebsiteUrl!;

        if (_options.Icons.Count > 0)
            info["icons"] = McpIcon.ResolveIcons(_options.Icons, _httpContextAccessor?.HttpContext);

        return info;
    }

    private Dictionary<string, object> BuildServerCapabilities(bool includeExtensions)
    {
        var capabilities = new Dictionary<string, object>
        {
            ["tools"] = new { listChanged = true },
            ["resources"] = new { subscribe = false, listChanged = true },
            ["prompts"] = new { listChanged = true },
            ["logging"] = new { }
        };

        if (includeExtensions && _options.EnableTasksExtension)
        {
            capabilities["extensions"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/tasks"] = new { }
            };
        }

        return capabilities;
    }

    private object HandleServerDiscover(JsonElement paramsElement)
    {
        var serverInfo = BuildServerInfo();
        var response = new Dictionary<string, object>
        {
            ["resultType"] = "complete",
            ["supportedVersions"] = _options.SupportedProtocolVersions.ToArray(),
            ["capabilities"] = BuildServerCapabilities(includeExtensions: true),
            ["ttlMs"] = _options.ListCacheTtlMs ?? 60000,
            ["cacheScope"] = _options.CacheScope,
            ["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/serverInfo"] = serverInfo
            }
        };

        if (!string.IsNullOrEmpty(_options.Instructions))
        {
            response["instructions"] = _options.Instructions!;
        }

        return response;
    }

    /// <summary>
    /// First protocol version that replaced the 'initialize' handshake with per-request '_meta' (SEP-2567).
    /// Per MCP 2026-07-28 and rmcp (ProtocolVersion::NO_INITIALIZE / LATEST_WITH_INITIALIZE),
    /// 'initialize' only negotiates versions prior to 2026-07-28 (ceiling '2025-11-25'),
    /// whereas 2026-07-28 uses 'server/discover' and stateless per-request '_meta'.
    /// </summary>
    internal const string NoInitializeVersion = "2026-07-28";

    internal static bool HasInitializeHandshake(string version) =>
        string.CompareOrdinal(version, NoInitializeVersion) < 0;

    internal static string NegotiateInitializeVersion(string? clientVersion, McpServerOptions options)
    {
        if (!string.IsNullOrEmpty(clientVersion) &&
            HasInitializeHandshake(clientVersion) &&
            options.SupportedProtocolVersions.Contains(clientVersion))
        {
            return clientVersion;
        }

        if (HasInitializeHandshake(options.ProtocolVersion) &&
            options.SupportedProtocolVersions.Contains(options.ProtocolVersion))
        {
            return options.ProtocolVersion;
        }

        return options.SupportedProtocolVersions
            .Where(HasInitializeHandshake)
            .OrderByDescending(v => v, StringComparer.Ordinal)
            .FirstOrDefault() ?? "2025-11-25";
    }

    private object HandleInitialize(JsonElement paramsElement)
    {
        string? requestedVersion = null;

        if (paramsElement.ValueKind == JsonValueKind.Object)
        {
            if (paramsElement.TryGetProperty("protocolVersion", out var clientVersionEl))
            {
                requestedVersion = clientVersionEl.GetString();
            }
            else if (paramsElement.TryGetProperty("_meta", out var metaEl) &&
                     metaEl.ValueKind == JsonValueKind.Object &&
                     metaEl.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var metaVersionEl))
            {
                requestedVersion = metaVersionEl.GetString();
            }
        }

        string negotiatedVersion = NegotiateInitializeVersion(requestedVersion, _options);

        if (paramsElement.ValueKind == JsonValueKind.Object &&
            _options.EnableLogging &&
            paramsElement.TryGetProperty("clientInfo", out var clientInfo) &&
            clientInfo.ValueKind == JsonValueKind.Object)
        {
            var name = clientInfo.TryGetProperty("name", out var n) ? n.GetString() : "Unknown";
            var version = clientInfo.TryGetProperty("version", out var v) ? v.GetString() : "Unknown";
            Console.WriteLine($"[MCP] Client connected: {name} {version} (protocol: {negotiatedVersion})");
        }

        var response = new Dictionary<string, object>
        {
            ["protocolVersion"] = negotiatedVersion,
            ["capabilities"] = BuildServerCapabilities(includeExtensions: false),
            ["serverInfo"] = BuildServerInfo()
        };

        if (!string.IsNullOrEmpty(_options.Instructions))
        {
            response["instructions"] = _options.Instructions!;
        }

        return response;
    }

    private async Task<object> HandleToolCallAsync(JsonElement paramsElement, CancellationToken cancellationToken)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object || !paramsElement.TryGetProperty("name", out var nameEl))
            throw McpException.InvalidParams("Tool call requires a 'name' parameter.");

        var toolName = nameEl.GetString() ?? "";
        paramsElement.TryGetProperty("arguments", out var args);

        if (_options.EnableLogging)
            Console.WriteLine($"[MCP] Tool call: {toolName}");

        if (!_registry.Tools.TryGetValue(toolName, out var toolRegistration))
        {
            throw McpException.InvalidParams($"Unknown tool: {toolName}");
        }

        // Check if client requested asynchronous task execution (io.modelcontextprotocol/tasks - SEP-2663)
        if (_options.EnableTasksExtension && IsAsyncTaskRequested(paramsElement, out var requestedTtl))
        {
            var mcpTask = _taskStore.CreateTask(ttl: requestedTtl ?? 60000, pollInterval: 1000, statusMessage: $"Executing tool '{toolName}'");
            var clonedArgs = args.ValueKind != JsonValueKind.Undefined ? args.Clone() : default;
            _ = Task.Run(async () =>
            {
                try
                {
                    var taskResult = await ExecuteToolFormattedAsync(toolName, clonedArgs, mcpTask.CancellationTokenSource!.Token);
                    _taskStore.CompleteTask(mcpTask.TaskId, taskResult);
                }
                catch (OperationCanceledException)
                {
                    _taskStore.CancelTask(mcpTask.TaskId, "Tool execution cancelled.");
                }
                catch (Exception ex)
                {
                    _taskStore.FailTask(mcpTask.TaskId, ex.Message);
                }
            });

            return new
            {
                resultType = "task",
                taskId = mcpTask.TaskId,
                status = mcpTask.Status,
                statusMessage = mcpTask.StatusMessage,
                createdAt = mcpTask.CreatedAt,
                lastUpdatedAt = mcpTask.LastUpdatedAt,
                ttl = mcpTask.Ttl,
                ttlMs = mcpTask.TtlMs,
                pollInterval = mcpTask.PollInterval,
                pollIntervalMs = mcpTask.PollIntervalMs,
                task = mcpTask
            };
        }

        return await ExecuteToolFormattedAsync(toolName, args, cancellationToken);
    }

    private static bool IsAsyncTaskRequested(JsonElement paramsElement, out int? ttl)
    {
        ttl = null;
        if (paramsElement.TryGetProperty("task", out var taskEl) && taskEl.ValueKind == JsonValueKind.Object)
        {
            if (taskEl.TryGetProperty("ttl", out var ttlEl) && ttlEl.ValueKind == JsonValueKind.Number)
                ttl = ttlEl.GetInt32();
            else if (taskEl.TryGetProperty("ttlMs", out var ttlMsEl) && ttlMsEl.ValueKind == JsonValueKind.Number)
                ttl = ttlMsEl.GetInt32();
            return true;
        }

        if (paramsElement.TryGetProperty("_meta", out var metaEl) &&
            metaEl.ValueKind == JsonValueKind.Object &&
            metaEl.TryGetProperty("io.modelcontextprotocol/tasks", out var metaTaskEl))
        {
            if (metaTaskEl.ValueKind == JsonValueKind.Object)
            {
                if (metaTaskEl.TryGetProperty("ttl", out var ttlEl) && ttlEl.ValueKind == JsonValueKind.Number)
                    ttl = ttlEl.GetInt32();
                else if (metaTaskEl.TryGetProperty("ttlMs", out var ttlMsEl) && ttlMsEl.ValueKind == JsonValueKind.Number)
                    ttl = ttlMsEl.GetInt32();
            }
            return metaTaskEl.ValueKind == JsonValueKind.Object || metaTaskEl.ValueKind == JsonValueKind.True;
        }

        return false;
    }

    private async Task<object> ExecuteToolFormattedAsync(string toolName, JsonElement args, CancellationToken cancellationToken)
    {
        try
        {
            var rawResult = await _registry.InvokeToolRawAsync(toolName, args, cancellationToken);

            if (rawResult is McpToolResult toolResult)
            {
                return toolResult;
            }

            if (rawResult is McpContent singleContent)
            {
                return new { content = new[] { singleContent }, isError = false };
            }

            if (rawResult is IEnumerable<McpContent> contentList)
            {
                return new { content = contentList.ToArray(), isError = false };
            }

            if (rawResult != null && McpRegistry.IsStructuredObjectType(rawResult.GetType()))
            {
                var serialized = JsonSerializer.Serialize(rawResult);
                return new
                {
                    content = new[] { new { type = "text", text = serialized } },
                    structuredContent = rawResult,
                    isError = false
                };
            }

            var textResult = rawResult?.ToString() ?? "";
            return new { content = new[] { new { type = "text", text = textResult } }, isError = false };
        }
        catch (Exception ex)
        {
            // Per MCP 2025-11-25+ specification: input validation and execution errors inside a valid tool
            // are returned as Tool Execution Errors (isError = true) so LLM agents (like Devin) can self-correct.
            return new { content = new[] { new { type = "text", text = $"Error: {ex.Message}" } }, isError = true };
        }
    }

    private object HandleTasksGet(JsonElement paramsElement)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object || !paramsElement.TryGetProperty("taskId", out var idEl))
            throw McpException.InvalidParams("tasks/get requires a 'taskId' parameter.");

        var taskId = idEl.GetString() ?? "";
        var task = _taskStore.GetTask(taskId) ?? throw McpException.InvalidParams($"Unknown taskId: {taskId}");
        return task;
    }

    private object HandleTasksList()
    {
        return new { tasks = _taskStore.Tasks.Values.ToArray() };
    }

    private object HandleTasksUpdate(JsonElement paramsElement)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object || !paramsElement.TryGetProperty("taskId", out var idEl))
            throw McpException.InvalidParams("tasks/update requires a 'taskId' parameter.");

        var taskId = idEl.GetString() ?? "";
        var status = paramsElement.TryGetProperty("status", out var sEl) ? sEl.GetString() : null;
        var statusMessage = paramsElement.TryGetProperty("statusMessage", out var smEl) ? smEl.GetString() : null;
        object? result = paramsElement.TryGetProperty("result", out var rEl) ? rEl.Clone() : null;

        var task = _taskStore.UpdateTask(taskId, status, statusMessage, result)
            ?? throw McpException.InvalidParams($"Unknown taskId: {taskId}");
        return task;
    }

    private object HandleTasksCancel(JsonElement paramsElement)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object || !paramsElement.TryGetProperty("taskId", out var idEl))
            throw McpException.InvalidParams("tasks/cancel requires a 'taskId' parameter.");

        var taskId = idEl.GetString() ?? "";
        var task = _taskStore.CancelTask(taskId) ?? throw McpException.InvalidParams($"Unknown taskId: {taskId}");
        return task;
    }

    private async Task<object> HandleResourceReadAsync(JsonElement paramsElement, CancellationToken cancellationToken)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object || !paramsElement.TryGetProperty("uri", out var uriEl))
            throw McpException.InvalidParams("Resource read requires a 'uri' parameter.");

        var uri = uriEl.GetString() ?? "";

        if (_options.EnableLogging)
            Console.WriteLine($"[MCP] Resource read: {uri}");

        var (content, mimeType) = await _registry.ReadResourceAsync(uri, cancellationToken);
        return new { contents = new[] { new { uri, mimeType, text = content } } };
    }

    private async Task<object> HandlePromptGetAsync(JsonElement paramsElement, CancellationToken cancellationToken)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object || !paramsElement.TryGetProperty("name", out var nameEl))
            throw McpException.InvalidParams("Prompt get requires a 'name' parameter.");

        var name = nameEl.GetString() ?? "";
        paramsElement.TryGetProperty("arguments", out var args);

        if (_options.EnableLogging)
            Console.WriteLine($"[MCP] Prompt get: {name}");

        var text = await _registry.GetPromptAsync(name, args, cancellationToken);
        return new
        {
            description = name,
            messages = new[] { new { role = "user", content = new { type = "text", text } } }
        };
    }
}
