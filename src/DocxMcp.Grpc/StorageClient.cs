using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Grpc;

/// <summary>
/// High-level client wrapper for the gRPC storage service.
/// Handles streaming for large files and provides a simple API.
/// </summary>
public sealed class StorageClient : IStorageClient
{
    private readonly GrpcChannel _channel;
    private readonly StorageService.StorageServiceClient _client;
    private readonly ILogger<StorageClient>? _logger;
    private readonly int _chunkSize;

    /// <summary>
    /// Default chunk size for streaming uploads: 256KB
    /// </summary>
    public const int DefaultChunkSize = 256 * 1024;

    public StorageClient(GrpcChannel channel, ILogger<StorageClient>? logger = null, int chunkSize = DefaultChunkSize)
    {
        _channel = channel;
        _client = new StorageService.StorageServiceClient(channel);
        _logger = logger;
        _chunkSize = chunkSize;
    }

    /// <summary>
    /// Create a StorageClient from options.
    /// </summary>
    public static async Task<StorageClient> CreateAsync(
        StorageClientOptions options,
        GrpcLauncher? launcher = null,
        ILogger<StorageClient>? logger = null,
        CancellationToken cancellationToken = default)
    {
        string address;

        if (!string.IsNullOrEmpty(options.ServerUrl))
        {
            address = options.ServerUrl;
        }
        else if (launcher is not null)
        {
            address = await launcher.EnsureServerRunningAsync(cancellationToken);
        }
        else
        {
            throw new InvalidOperationException(
                "Either ServerUrl must be configured or a GrpcLauncher must be provided for auto-launch.");
        }

        var channel = GrpcChannel.ForAddress(address);
        return new StorageClient(channel, logger);
    }

    // =========================================================================
    // Session Operations
    // =========================================================================

    /// <summary>
    /// Load a session's DOCX bytes (streaming download).
    /// </summary>
    public async Task<(byte[]? Data, bool Found)> LoadSessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new LoadSessionRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        using var call = _client.LoadSession(request, cancellationToken: cancellationToken);

        var data = new List<byte>();
        bool found = false;
        bool isFirst = true;

        await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            if (isFirst)
            {
                found = chunk.Found;
                isFirst = false;

                if (!found)
                    return (null, false);
            }

            data.AddRange(chunk.Data);
        }

        _logger?.LogDebug("Loaded session {SessionId} for tenant {TenantId} ({Bytes} bytes)",
            sessionId, tenantId, data.Count);

        return (data.ToArray(), found);
    }

    /// <summary>
    /// Save a session's DOCX bytes (streaming upload).
    /// </summary>
    public async Task SaveSessionAsync(
        string tenantId,
        string sessionId,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        using var call = _client.SaveSession(cancellationToken: cancellationToken);

        var chunks = ChunkData(data);
        bool isFirst = true;

        foreach (var (chunk, isLast) in chunks)
        {
            var msg = new SaveSessionChunk
            {
                Data = Google.Protobuf.ByteString.CopyFrom(chunk),
                IsLast = isLast
            };

            if (isFirst)
            {
                msg.Context = new TenantContext { TenantId = tenantId };
                msg.SessionId = sessionId;
                isFirst = false;
            }

            await call.RequestStream.WriteAsync(msg, cancellationToken);
        }

        await call.RequestStream.CompleteAsync();
        var response = await call;

        if (!response.Success)
        {
            throw new InvalidOperationException($"Failed to save session {sessionId}");
        }

        _logger?.LogDebug("Saved session {SessionId} for tenant {TenantId} ({Bytes} bytes)",
            sessionId, tenantId, data.Length);
    }

    /// <summary>
    /// List all sessions for a tenant.
    /// </summary>
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var request = new ListSessionsRequest
        {
            Context = new TenantContext { TenantId = tenantId }
        };

        var response = await _client.ListSessionsAsync(request, cancellationToken: cancellationToken);
        return response.Sessions;
    }

    /// <summary>
    /// Delete a session.
    /// </summary>
    public async Task<bool> DeleteSessionAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new DeleteSessionRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await _client.DeleteSessionAsync(request, cancellationToken: cancellationToken);
        return response.Existed;
    }

    /// <summary>
    /// Check if a session exists.
    /// </summary>
    public async Task<bool> SessionExistsAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new SessionExistsRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await _client.SessionExistsAsync(request, cancellationToken: cancellationToken);
        return response.Exists;
    }

    // =========================================================================
    // Index Operations
    // =========================================================================

    /// <summary>
    /// Load the session index.
    /// </summary>
    public async Task<(byte[]? Data, bool Found)> LoadIndexAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var request = new LoadIndexRequest
        {
            Context = new TenantContext { TenantId = tenantId }
        };

        var response = await _client.LoadIndexAsync(request, cancellationToken: cancellationToken);

        if (!response.Found)
            return (null, false);

        return (response.IndexJson.ToByteArray(), true);
    }

    /// <summary>
    /// Save the session index.
    /// </summary>
    public async Task SaveIndexAsync(
        string tenantId,
        byte[] indexJson,
        CancellationToken cancellationToken = default)
    {
        var request = new SaveIndexRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            IndexJson = Google.Protobuf.ByteString.CopyFrom(indexJson)
        };

        var response = await _client.SaveIndexAsync(request, cancellationToken: cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException("Failed to save index");
        }
    }

    // =========================================================================
    // WAL Operations
    // =========================================================================

    /// <summary>
    /// Append entries to the WAL.
    /// </summary>
    public async Task<ulong> AppendWalAsync(
        string tenantId,
        string sessionId,
        IEnumerable<WalEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var request = new AppendWalRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };
        request.Entries.AddRange(entries);

        var response = await _client.AppendWalAsync(request, cancellationToken: cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException($"Failed to append WAL for session {sessionId}");
        }

        return response.NewPosition;
    }

    /// <summary>
    /// Read WAL entries.
    /// </summary>
    public async Task<(IReadOnlyList<WalEntry> Entries, bool HasMore)> ReadWalAsync(
        string tenantId,
        string sessionId,
        ulong fromPosition = 0,
        ulong limit = 0,
        CancellationToken cancellationToken = default)
    {
        var request = new ReadWalRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            FromPosition = fromPosition,
            Limit = limit
        };

        var response = await _client.ReadWalAsync(request, cancellationToken: cancellationToken);
        return (response.Entries, response.HasMore);
    }

    /// <summary>
    /// Truncate WAL entries.
    /// </summary>
    public async Task<ulong> TruncateWalAsync(
        string tenantId,
        string sessionId,
        ulong keepFromPosition,
        CancellationToken cancellationToken = default)
    {
        var request = new TruncateWalRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            KeepFromPosition = keepFromPosition
        };

        var response = await _client.TruncateWalAsync(request, cancellationToken: cancellationToken);
        return response.EntriesRemoved;
    }

    // =========================================================================
    // Checkpoint Operations
    // =========================================================================

    /// <summary>
    /// Save a checkpoint (streaming upload).
    /// </summary>
    public async Task SaveCheckpointAsync(
        string tenantId,
        string sessionId,
        ulong position,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        using var call = _client.SaveCheckpoint(cancellationToken: cancellationToken);

        var chunks = ChunkData(data);
        bool isFirst = true;

        foreach (var (chunk, isLast) in chunks)
        {
            var msg = new SaveCheckpointChunk
            {
                Data = Google.Protobuf.ByteString.CopyFrom(chunk),
                IsLast = isLast
            };

            if (isFirst)
            {
                msg.Context = new TenantContext { TenantId = tenantId };
                msg.SessionId = sessionId;
                msg.Position = position;
                isFirst = false;
            }

            await call.RequestStream.WriteAsync(msg, cancellationToken);
        }

        await call.RequestStream.CompleteAsync();
        var response = await call;

        if (!response.Success)
        {
            throw new InvalidOperationException($"Failed to save checkpoint at position {position}");
        }

        _logger?.LogDebug("Saved checkpoint at position {Position} for session {SessionId} ({Bytes} bytes)",
            position, sessionId, data.Length);
    }

    /// <summary>
    /// Load a checkpoint (streaming download).
    /// </summary>
    public async Task<(byte[]? Data, ulong Position, bool Found)> LoadCheckpointAsync(
        string tenantId,
        string sessionId,
        ulong position = 0,
        CancellationToken cancellationToken = default)
    {
        var request = new LoadCheckpointRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            Position = position
        };

        using var call = _client.LoadCheckpoint(request, cancellationToken: cancellationToken);

        var data = new List<byte>();
        bool found = false;
        ulong actualPosition = 0;
        bool isFirst = true;

        await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            if (isFirst)
            {
                found = chunk.Found;
                actualPosition = chunk.Position;
                isFirst = false;

                if (!found)
                    return (null, 0, false);
            }

            data.AddRange(chunk.Data);
        }

        _logger?.LogDebug("Loaded checkpoint at position {Position} for session {SessionId} ({Bytes} bytes)",
            actualPosition, sessionId, data.Count);

        return (data.ToArray(), actualPosition, found);
    }

    /// <summary>
    /// List checkpoints for a session.
    /// </summary>
    public async Task<IReadOnlyList<CheckpointInfo>> ListCheckpointsAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new ListCheckpointsRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await _client.ListCheckpointsAsync(request, cancellationToken: cancellationToken);
        return response.Checkpoints;
    }

    // =========================================================================
    // Lock Operations
    // =========================================================================

    /// <summary>
    /// Acquire a lock.
    /// </summary>
    public async Task<(bool Acquired, string? CurrentHolder, long ExpiresAt)> AcquireLockAsync(
        string tenantId,
        string resourceId,
        string holderId,
        int ttlSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var request = new AcquireLockRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            ResourceId = resourceId,
            HolderId = holderId,
            TtlSeconds = ttlSeconds
        };

        var response = await _client.AcquireLockAsync(request, cancellationToken: cancellationToken);

        return (
            response.Acquired,
            string.IsNullOrEmpty(response.CurrentHolder) ? null : response.CurrentHolder,
            response.ExpiresAtUnix
        );
    }

    /// <summary>
    /// Release a lock.
    /// </summary>
    public async Task<(bool Released, string Reason)> ReleaseLockAsync(
        string tenantId,
        string resourceId,
        string holderId,
        CancellationToken cancellationToken = default)
    {
        var request = new ReleaseLockRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            ResourceId = resourceId,
            HolderId = holderId
        };

        var response = await _client.ReleaseLockAsync(request, cancellationToken: cancellationToken);
        return (response.Released, response.Reason);
    }

    /// <summary>
    /// Renew a lock.
    /// </summary>
    public async Task<(bool Renewed, long ExpiresAt, string Reason)> RenewLockAsync(
        string tenantId,
        string resourceId,
        string holderId,
        int ttlSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var request = new RenewLockRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            ResourceId = resourceId,
            HolderId = holderId,
            TtlSeconds = ttlSeconds
        };

        var response = await _client.RenewLockAsync(request, cancellationToken: cancellationToken);
        return (response.Renewed, response.ExpiresAtUnix, response.Reason);
    }

    // =========================================================================
    // Health Check
    // =========================================================================

    /// <summary>
    /// Check server health.
    /// </summary>
    public async Task<(bool Healthy, string Backend, string Version)> HealthCheckAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await _client.HealthCheckAsync(new HealthCheckRequest(), cancellationToken: cancellationToken);
        return (response.Healthy, response.Backend, response.Version);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private IEnumerable<(byte[] Chunk, bool IsLast)> ChunkData(byte[] data)
    {
        if (data.Length == 0)
        {
            yield return (Array.Empty<byte>(), true);
            yield break;
        }

        int offset = 0;
        while (offset < data.Length)
        {
            int remaining = data.Length - offset;
            int size = Math.Min(_chunkSize, remaining);
            bool isLast = offset + size >= data.Length;

            var chunk = new byte[size];
            Array.Copy(data, offset, chunk, 0, size);

            yield return (chunk, isLast);
            offset += size;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await Task.CompletedTask;
    }
}
