using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace McpHttpServer;

/// <summary>
/// Represents a durable/asynchronous MCP task under the io.modelcontextprotocol/tasks extension (MCP 2026-07-28 / SEP-2663).
/// </summary>
public class McpTask
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    /// <summary>
    /// Current state: "working", "input_required", "completed", "failed", or "cancelled".
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "working";

    [JsonPropertyName("statusMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusMessage { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("lastUpdatedAt")]
    public string LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ttl { get; set; }

    [JsonPropertyName("pollInterval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PollInterval { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Error { get; set; }

    [JsonIgnore]
    internal CancellationTokenSource? CancellationTokenSource { get; set; }
}

/// <summary>
/// Thread-safe store managing asynchronous tasks for the io.modelcontextprotocol/tasks extension (SEP-2663).
/// </summary>
public class McpTaskStore
{
    private readonly ConcurrentDictionary<string, McpTask> _tasks = new();

    public IReadOnlyDictionary<string, McpTask> Tasks => _tasks;

    /// <summary>
    /// Creates and registers a new task in the "working" state.
    /// </summary>
    public McpTask CreateTask(int? ttl = 60000, int? pollInterval = 1000, string? statusMessage = null)
    {
        var cts = new CancellationTokenSource();
        var task = new McpTask
        {
            TaskId = Guid.NewGuid().ToString("N"),
            Status = "working",
            StatusMessage = statusMessage,
            Ttl = ttl,
            PollInterval = pollInterval,
            CancellationTokenSource = cts
        };
        _tasks[task.TaskId] = task;
        return task;
    }

    /// <summary>
    /// Gets a task by ID, or null if not found.
    /// </summary>
    public McpTask? GetTask(string taskId)
    {
        return _tasks.TryGetValue(taskId, out var task) ? task : null;
    }

    /// <summary>
    /// Completes a task with the given result payload.
    /// </summary>
    public bool CompleteTask(string taskId, object? result, string? statusMessage = null)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return false;

        task.Status = "completed";
        task.Result = result;
        task.StatusMessage = statusMessage;
        task.LastUpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        return true;
    }

    /// <summary>
    /// Marks a task as failed with an error message.
    /// </summary>
    public bool FailTask(string taskId, string errorMessage, int errorCode = -32603)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return false;

        task.Status = "failed";
        task.StatusMessage = errorMessage;
        task.Error = new { code = errorCode, message = errorMessage };
        task.LastUpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        return true;
    }

    /// <summary>
    /// Updates an interactive task state or status message (tasks/update in SEP-2663).
    /// </summary>
    public McpTask? UpdateTask(string taskId, string? status = null, string? statusMessage = null, object? result = null)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;

        if (!string.IsNullOrEmpty(status))
            task.Status = status;

        if (statusMessage != null)
            task.StatusMessage = statusMessage;

        if (result != null)
            task.Result = result;

        task.LastUpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        return task;
    }

    /// <summary>
    /// Cancels a running task (tasks/cancel in SEP-2663).
    /// </summary>
    public McpTask? CancelTask(string taskId, string? reason = null)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;

        if (task.Status is "working" or "input_required")
        {
            task.CancellationTokenSource?.Cancel();
            task.Status = "cancelled";
            task.StatusMessage = reason ?? "Task was cancelled by the client.";
            task.LastUpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        }

        return task;
    }
}
