using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DocxMcp.Auth;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Transport;

/// <summary>
/// MCP transport implementation using Server-Sent Events (SSE).
/// Handles bidirectional communication: SSE for server→client, HTTP POST for client→server.
/// </summary>
public sealed class SseTransport : IAsyncDisposable
{
    private readonly ILogger<SseTransport> _logger;
    private readonly ConcurrentDictionary<string, SseConnection> _connections = new();

    public SseTransport(ILogger<SseTransport> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Create a new SSE connection for a user.
    /// </summary>
    public SseConnection CreateConnection(string connectionId, UserContext user)
    {
        var connection = new SseConnection(connectionId, user, _logger);
        _connections[connectionId] = connection;
        _logger.LogInformation("SSE connection created: {ConnectionId} for user {UserId}", connectionId, user.UserId);
        return connection;
    }

    /// <summary>
    /// Get an existing connection by ID.
    /// </summary>
    public SseConnection? GetConnection(string connectionId)
    {
        return _connections.TryGetValue(connectionId, out var connection) ? connection : null;
    }

    /// <summary>
    /// Remove a connection.
    /// </summary>
    public async Task RemoveConnectionAsync(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            await connection.DisposeAsync();
            _logger.LogInformation("SSE connection removed: {ConnectionId}", connectionId);
        }
    }

    /// <summary>
    /// Get all active connections.
    /// </summary>
    public IEnumerable<SseConnection> GetAllConnections() => _connections.Values;

    /// <summary>
    /// Get connections for a specific user.
    /// </summary>
    public IEnumerable<SseConnection> GetConnectionsForUser(string userId)
    {
        return _connections.Values.Where(c => c.User.UserId == userId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }
        _connections.Clear();
    }
}

/// <summary>
/// Represents a single SSE connection to a client.
/// </summary>
public sealed class SseConnection : IAsyncDisposable
{
    private readonly Channel<SseMessage> _outboundChannel;
    private readonly Channel<JsonDocument> _inboundChannel;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public string ConnectionId { get; }
    public UserContext User { get; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; private set; } = DateTime.UtcNow;

    public SseConnection(string connectionId, UserContext user, ILogger logger)
    {
        ConnectionId = connectionId;
        User = user;
        _logger = logger;
        _outboundChannel = Channel.CreateBounded<SseMessage>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _inboundChannel = Channel.CreateBounded<JsonDocument>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Send a message to the client via SSE.
    /// </summary>
    public async ValueTask SendAsync(string eventType, object data, CancellationToken ct = default)
    {
        if (_disposed) return;

        var message = new SseMessage
        {
            Event = eventType,
            Data = JsonSerializer.Serialize(data),
            Id = Guid.NewGuid().ToString("N")[..8]
        };

        await _outboundChannel.Writer.WriteAsync(message, ct);
        LastActivityAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Send a raw JSON-RPC message.
    /// </summary>
    public async ValueTask SendJsonRpcAsync(JsonDocument message, CancellationToken ct = default)
    {
        if (_disposed) return;

        var sseMessage = new SseMessage
        {
            Event = "message",
            Data = message.RootElement.GetRawText(),
            Id = Guid.NewGuid().ToString("N")[..8]
        };

        await _outboundChannel.Writer.WriteAsync(sseMessage, ct);
        LastActivityAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Receive a message from the client (via HTTP POST).
    /// </summary>
    public async ValueTask ReceiveAsync(JsonDocument message, CancellationToken ct = default)
    {
        if (_disposed) return;

        await _inboundChannel.Writer.WriteAsync(message, ct);
        LastActivityAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Read inbound messages (for MCP server processing).
    /// </summary>
    public IAsyncEnumerable<JsonDocument> ReadInboundAsync(CancellationToken ct = default)
    {
        return _inboundChannel.Reader.ReadAllAsync(ct);
    }

    /// <summary>
    /// Stream SSE messages to the HTTP response.
    /// </summary>
    public async Task StreamToResponseAsync(Stream responseStream, CancellationToken ct)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var writer = new StreamWriter(responseStream, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };

        try
        {
            // Send initial connection event
            await WriteEventAsync(writer, new SseMessage
            {
                Event = "connected",
                Data = JsonSerializer.Serialize(new { connectionId = ConnectionId }),
                Id = "0"
            });

            // Stream messages
            await foreach (var message in _outboundChannel.Reader.ReadAllAsync(linkedCts.Token))
            {
                await WriteEventAsync(writer, message);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SSE stream cancelled for connection {ConnectionId}", ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming SSE for connection {ConnectionId}", ConnectionId);
        }
    }

    private static async Task WriteEventAsync(StreamWriter writer, SseMessage message)
    {
        if (!string.IsNullOrEmpty(message.Id))
            await writer.WriteAsync($"id: {message.Id}\n");
        if (!string.IsNullOrEmpty(message.Event))
            await writer.WriteAsync($"event: {message.Event}\n");

        // Split data by newlines for SSE format
        var lines = message.Data.Split('\n');
        foreach (var line in lines)
        {
            await writer.WriteAsync($"data: {line}\n");
        }
        await writer.WriteAsync("\n");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _outboundChannel.Writer.TryComplete();
        _inboundChannel.Writer.TryComplete();
        _cts.Dispose();

        await Task.CompletedTask;
    }
}

/// <summary>
/// SSE message format.
/// </summary>
public sealed class SseMessage
{
    public string? Id { get; init; }
    public string? Event { get; init; }
    public string Data { get; init; } = string.Empty;
}
