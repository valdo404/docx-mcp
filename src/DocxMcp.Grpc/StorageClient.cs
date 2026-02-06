using System.Net.Sockets;
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

        GrpcChannel channel;

        if (address.StartsWith("unix://"))
        {
            // Unix Domain Socket requires a custom SocketsHttpHandler
            var socketPath = address.Substring("unix://".Length);

            var connectionFactory = new UnixDomainSocketConnectionFactory(socketPath);
            var socketsHandler = new SocketsHttpHandler
            {
                ConnectCallback = connectionFactory.ConnectAsync
            };

            channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = socketsHandler
            });
        }
        else
        {
            channel = GrpcChannel.ForAddress(address);
        }

        return new StorageClient(channel, logger);
    }

    /// <summary>
    /// Connection factory for Unix Domain Sockets.
    /// </summary>
    private sealed class UnixDomainSocketConnectionFactory(string socketPath)
    {
        public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
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
    public async Task<IReadOnlyList<SessionInfoDto>> ListSessionsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var request = new ListSessionsRequest
        {
            Context = new TenantContext { TenantId = tenantId }
        };

        var response = await _client.ListSessionsAsync(request, cancellationToken: cancellationToken);
        return response.Sessions.Select(s => new SessionInfoDto(
            s.SessionId,
            string.IsNullOrEmpty(s.SourcePath) ? null : s.SourcePath,
            DateTimeOffset.FromUnixTimeSeconds(s.CreatedAtUnix).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(s.ModifiedAtUnix).UtcDateTime,
            s.SizeBytes
        )).ToList();
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
    // Index Operations (Atomic - server handles locking internally)
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
    /// Atomically add a session to the index.
    /// </summary>
    public async Task<(bool Success, bool AlreadyExists)> AddSessionToIndexAsync(
        string tenantId,
        string sessionId,
        SessionIndexEntryDto entry,
        CancellationToken cancellationToken = default)
    {
        var request = new AddSessionToIndexRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            Entry = new SessionIndexEntry
            {
                SourcePath = entry.SourcePath ?? "",
                CreatedAtUnix = new DateTimeOffset(entry.CreatedAt).ToUnixTimeSeconds(),
                ModifiedAtUnix = new DateTimeOffset(entry.ModifiedAt).ToUnixTimeSeconds(),
                WalPosition = entry.WalPosition
            }
        };
        request.Entry.CheckpointPositions.AddRange(entry.CheckpointPositions);

        var response = await _client.AddSessionToIndexAsync(request, cancellationToken: cancellationToken);
        return (response.Success, response.AlreadyExists);
    }

    /// <summary>
    /// Atomically update a session in the index.
    /// </summary>
    public async Task<(bool Success, bool NotFound)> UpdateSessionInIndexAsync(
        string tenantId,
        string sessionId,
        long? modifiedAtUnix = null,
        ulong? walPosition = null,
        IEnumerable<ulong>? addCheckpointPositions = null,
        IEnumerable<ulong>? removeCheckpointPositions = null,
        CancellationToken cancellationToken = default)
    {
        var request = new UpdateSessionInIndexRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        if (modifiedAtUnix.HasValue)
            request.ModifiedAtUnix = modifiedAtUnix.Value;

        if (walPosition.HasValue)
            request.WalPosition = walPosition.Value;

        if (addCheckpointPositions is not null)
            request.AddCheckpointPositions.AddRange(addCheckpointPositions);

        if (removeCheckpointPositions is not null)
            request.RemoveCheckpointPositions.AddRange(removeCheckpointPositions);

        var response = await _client.UpdateSessionInIndexAsync(request, cancellationToken: cancellationToken);
        return (response.Success, response.NotFound);
    }

    /// <summary>
    /// Atomically remove a session from the index.
    /// </summary>
    public async Task<(bool Success, bool Existed)> RemoveSessionFromIndexAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new RemoveSessionFromIndexRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await _client.RemoveSessionFromIndexAsync(request, cancellationToken: cancellationToken);
        return (response.Success, response.Existed);
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
        IEnumerable<WalEntryDto> entries,
        CancellationToken cancellationToken = default)
    {
        var request = new AppendWalRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        foreach (var entry in entries)
        {
            request.Entries.Add(new WalEntry
            {
                Position = entry.Position,
                Operation = entry.Operation,
                Path = entry.Path,
                PatchJson = Google.Protobuf.ByteString.CopyFrom(entry.PatchJson),
                TimestampUnix = new DateTimeOffset(entry.Timestamp).ToUnixTimeSeconds()
            });
        }

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
    public async Task<(IReadOnlyList<WalEntryDto> Entries, bool HasMore)> ReadWalAsync(
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

        var entries = response.Entries.Select(e => new WalEntryDto(
            e.Position,
            e.Operation,
            e.Path,
            e.PatchJson.ToByteArray(),
            DateTimeOffset.FromUnixTimeSeconds(e.TimestampUnix).UtcDateTime
        )).ToList();

        return (entries, response.HasMore);
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
    public async Task<IReadOnlyList<CheckpointInfoDto>> ListCheckpointsAsync(
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
        return response.Checkpoints.Select(c => new CheckpointInfoDto(
            c.Position,
            DateTimeOffset.FromUnixTimeSeconds(c.CreatedAtUnix).UtcDateTime,
            c.SizeBytes
        )).ToList();
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
    // SourceSync Operations
    // =========================================================================

    private SourceSyncService.SourceSyncServiceClient GetSyncClient()
    {
        return new SourceSyncService.SourceSyncServiceClient(_channel);
    }

    /// <summary>
    /// Register a source for a session.
    /// </summary>
    public async Task<(bool Success, string Error)> RegisterSourceAsync(
        string tenantId,
        string sessionId,
        SourceType sourceType,
        string uri,
        bool autoSync,
        CancellationToken cancellationToken = default)
    {
        var request = new RegisterSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            Source = new SourceDescriptor
            {
                Type = sourceType,
                Uri = uri
            },
            AutoSync = autoSync
        };

        var response = await GetSyncClient().RegisterSourceAsync(request, cancellationToken: cancellationToken);
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Unregister a source for a session.
    /// </summary>
    public async Task<bool> UnregisterSourceAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new UnregisterSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await GetSyncClient().UnregisterSourceAsync(request, cancellationToken: cancellationToken);
        return response.Success;
    }

    /// <summary>
    /// Update source configuration for a session (change URI, toggle auto-sync).
    /// </summary>
    public async Task<(bool Success, string Error)> UpdateSourceAsync(
        string tenantId,
        string sessionId,
        SourceType? sourceType = null,
        string? uri = null,
        bool? autoSync = null,
        CancellationToken cancellationToken = default)
    {
        var request = new UpdateSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        if (sourceType.HasValue && uri is not null)
        {
            request.Source = new SourceDescriptor
            {
                Type = sourceType.Value,
                Uri = uri
            };
        }

        if (autoSync.HasValue)
        {
            request.AutoSync = autoSync.Value;
            request.UpdateAutoSync = true;
        }

        var response = await GetSyncClient().UpdateSourceAsync(request, cancellationToken: cancellationToken);
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Sync session data to its registered source (streaming upload).
    /// </summary>
    public async Task<(bool Success, string Error, long SyncedAtUnix)> SyncToSourceAsync(
        string tenantId,
        string sessionId,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        using var call = GetSyncClient().SyncToSource(cancellationToken: cancellationToken);

        var chunks = ChunkData(data);
        bool isFirst = true;

        foreach (var (chunk, isLast) in chunks)
        {
            var msg = new SyncToSourceChunk
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

        _logger?.LogDebug("Synced session {SessionId} for tenant {TenantId} ({Bytes} bytes, success={Success})",
            sessionId, tenantId, data.Length, response.Success);

        return (response.Success, response.Error, response.SyncedAtUnix);
    }

    /// <summary>
    /// Get sync status for a session.
    /// </summary>
    public async Task<SyncStatusDto?> GetSyncStatusAsync(
        string tenantId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var request = new GetSyncStatusRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await GetSyncClient().GetSyncStatusAsync(request, cancellationToken: cancellationToken);

        if (!response.Registered || response.Status is null)
            return null;

        var status = response.Status;
        return new SyncStatusDto(
            status.SessionId,
            (SourceType)(int)status.Source.Type,
            status.Source.Uri,
            status.AutoSyncEnabled,
            status.LastSyncedAtUnix > 0 ? status.LastSyncedAtUnix : null,
            status.HasPendingChanges,
            string.IsNullOrEmpty(status.LastError) ? null : status.LastError);
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
