using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Grpc;

/// <summary>
/// gRPC client for sync storage operations (SourceSyncService + ExternalWatchService).
/// Handles source registration, sync-to-source, and external file watching.
/// </summary>
public sealed class SyncStorageClient : ISyncStorage
{
    private readonly GrpcChannel _channel;
    private readonly ILogger<SyncStorageClient>? _logger;
    private readonly int _chunkSize;

    /// <summary>
    /// Default chunk size for streaming uploads: 256KB
    /// </summary>
    public const int DefaultChunkSize = 256 * 1024;

    public SyncStorageClient(GrpcChannel channel, ILogger<SyncStorageClient>? logger = null, int chunkSize = DefaultChunkSize)
    {
        _channel = channel;
        _logger = logger;
        _chunkSize = chunkSize;
    }

    private SourceSyncService.SourceSyncServiceClient GetSyncClient()
        => new SourceSyncService.SourceSyncServiceClient(_channel);

    private ExternalWatchService.ExternalWatchServiceClient GetWatchClient()
        => new ExternalWatchService.ExternalWatchServiceClient(_channel);

    // =========================================================================
    // SourceSync Operations
    // =========================================================================

    public async Task<(bool Success, string Error)> RegisterSourceAsync(
        string tenantId, string sessionId, SourceType sourceType, string uri, bool autoSync,
        CancellationToken cancellationToken = default)
    {
        var request = new RegisterSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            Source = new SourceDescriptor { Type = sourceType, Uri = uri },
            AutoSync = autoSync
        };

        var response = await GetSyncClient().RegisterSourceAsync(request, cancellationToken: cancellationToken);
        return (response.Success, response.Error);
    }

    public async Task<bool> UnregisterSourceAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var request = new UnregisterSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await GetSyncClient().UnregisterSourceAsync(request, cancellationToken: cancellationToken);
        return response.Success;
    }

    public async Task<(bool Success, string Error)> UpdateSourceAsync(
        string tenantId, string sessionId,
        SourceType? sourceType = null, string? uri = null, bool? autoSync = null,
        CancellationToken cancellationToken = default)
    {
        var request = new UpdateSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        if (sourceType.HasValue && uri is not null)
        {
            request.Source = new SourceDescriptor { Type = sourceType.Value, Uri = uri };
        }

        if (autoSync.HasValue)
        {
            request.AutoSync = autoSync.Value;
            request.UpdateAutoSync = true;
        }

        var response = await GetSyncClient().UpdateSourceAsync(request, cancellationToken: cancellationToken);
        return (response.Success, response.Error);
    }

    public async Task<(bool Success, string Error, long SyncedAtUnix)> SyncToSourceAsync(
        string tenantId, string sessionId, byte[] data,
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

    public async Task<SyncStatusDto?> GetSyncStatusAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
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
    // ExternalWatch Operations
    // =========================================================================

    public async Task<(bool Success, string WatchId, string Error)> StartWatchAsync(
        string tenantId, string sessionId, SourceType sourceType, string uri, int pollIntervalSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        var request = new StartWatchRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            Source = new SourceDescriptor { Type = sourceType, Uri = uri },
            PollIntervalSeconds = pollIntervalSeconds
        };

        var response = await GetWatchClient().StartWatchAsync(request, cancellationToken: cancellationToken);
        return (response.Success, response.WatchId, response.Error);
    }

    public async Task<bool> StopWatchAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var request = new StopWatchRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await GetWatchClient().StopWatchAsync(request, cancellationToken: cancellationToken);
        return response.Success;
    }

    public async Task<(bool HasChanges, SourceMetadataDto? Current, SourceMetadataDto? Known)> CheckForChangesAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var request = new CheckForChangesRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await GetWatchClient().CheckForChangesAsync(request, cancellationToken: cancellationToken);

        return (
            response.HasChanges,
            response.CurrentMetadata is not null ? ConvertMetadata(response.CurrentMetadata) : null,
            response.KnownMetadata is not null ? ConvertMetadata(response.KnownMetadata) : null
        );
    }

    public async Task<SourceMetadataDto?> GetSourceMetadataAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var request = new GetSourceMetadataRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        var response = await GetWatchClient().GetSourceMetadataAsync(request, cancellationToken: cancellationToken);

        if (!response.Success || response.Metadata is null)
            return null;

        return ConvertMetadata(response.Metadata);
    }

    public async IAsyncEnumerable<ExternalChangeEventDto> WatchChangesAsync(
        string tenantId, IEnumerable<string> sessionIds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new WatchChangesRequest
        {
            Context = new TenantContext { TenantId = tenantId }
        };
        request.SessionIds.AddRange(sessionIds);

        using var call = GetWatchClient().WatchChanges(request, cancellationToken: cancellationToken);

        await foreach (var evt in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            yield return new ExternalChangeEventDto(
                evt.SessionId,
                (ExternalChangeType)(int)evt.ChangeType,
                evt.OldMetadata is not null ? ConvertMetadata(evt.OldMetadata) : null,
                evt.NewMetadata is not null ? ConvertMetadata(evt.NewMetadata) : null,
                evt.DetectedAtUnix,
                string.IsNullOrEmpty(evt.NewUri) ? null : evt.NewUri
            );
        }
    }

    private static SourceMetadataDto ConvertMetadata(SourceMetadata metadata)
    {
        return new SourceMetadataDto(
            metadata.SizeBytes,
            metadata.ModifiedAtUnix,
            string.IsNullOrEmpty(metadata.Etag) ? null : metadata.Etag,
            string.IsNullOrEmpty(metadata.VersionId) ? null : metadata.VersionId,
            metadata.ContentHash.IsEmpty ? null : metadata.ContentHash.ToByteArray()
        );
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
