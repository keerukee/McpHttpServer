using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace McpHttpServer;

/// <summary>
/// Thread-safe HTTP Session for sending SSE events to connected clients in Streamable HTTP.
/// </summary>
public class HttpSession : IDisposable
{
    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _eventCounter;
    private bool _isDisposed;

    public string? LastEventId { get; init; }

    public HttpSession(HttpResponse response, string? lastEventId = null)
    {
        _response = response;
        LastEventId = lastEventId;
    }

    public async Task SendEventAsync(string eventType, string data, string? id = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(HttpSession));

        await _lock.WaitAsync();
        try
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(HttpSession));

            var eventId = id ?? Interlocked.Increment(ref _eventCounter).ToString();
            var message = $"id: {eventId}\nevent: {eventType}\ndata: {data}\n\n";
            await _response.WriteAsync(message);
            await _response.Body.FlushAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SendMessageAsync(object jsonRpcMessage)
    {
        var data = JsonSerializer.Serialize(jsonRpcMessage);
        await SendEventAsync("message", data);
    }

    public void Dispose()
    {
        _isDisposed = true;
        _lock.Dispose();
    }
}
