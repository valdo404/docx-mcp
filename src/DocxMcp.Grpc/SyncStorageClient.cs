using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Grpc;

/// <summary>
/// gRPC client for sync storage operations (SourceSyncService + ExternalWatchService).
/// Handles source registration, sync-to-source, external file watching, and connection browsing.
/// </summary>
public class SyncStorageClient : ISyncStorage
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
        string tenantId, string sessionId, SourceType sourceType,
        string? connectionId, string path, string? fileId, bool autoSync,
        CancellationToken cancellationToken = default)
    {
        var request = new RegisterSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            Source = new SourceDescriptor
            {
                Type = sourceType,
                ConnectionId = connectionId ?? "",
                Path = path,
                FileId = fileId ?? ""
            },
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
        SourceType? sourceType = null, string? connectionId = null,
        string? path = null, string? fileId = null, bool? autoSync = null,
        CancellationToken cancellationToken = default)
    {
        var request = new UpdateSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId
        };

        if (sourceType.HasValue && path is not null)
        {
            request.Source = new SourceDescriptor
            {
                Type = sourceType.Value,
                ConnectionId = connectionId ?? "",
                Path = path,
                FileId = fileId ?? ""
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
            string.IsNullOrEmpty(status.Source.ConnectionId) ? null : status.Source.ConnectionId,
            status.Source.Path,
            string.IsNullOrEmpty(status.Source.FileId) ? null : status.Source.FileId,
            status.AutoSyncEnabled,
            status.LastSyncedAtUnix > 0 ? status.LastSyncedAtUnix : null,
            status.HasPendingChanges,
            string.IsNullOrEmpty(status.LastError) ? null : status.LastError);
    }

    // =========================================================================
    // ExternalWatch Operations
    // =========================================================================

    public async Task<(bool Success, string WatchId, string Error)> StartWatchAsync(
        string tenantId, string sessionId, SourceType sourceType,
        string? connectionId, string path, string? fileId, int pollIntervalSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        var request = new StartWatchRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            SessionId = sessionId,
            Source = new SourceDescriptor
            {
                Type = sourceType,
                ConnectionId = connectionId ?? "",
                Path = path,
                FileId = fileId ?? ""
            },
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

    // =========================================================================
    // Browse Operations
    // =========================================================================

    public async Task<List<ConnectionInfoDto>> ListConnectionsAsync(
        string tenantId, SourceType? filterType = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ListConnectionsRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            FilterType = filterType ?? 0
        };

        var response = await GetSyncClient().ListConnectionsAsync(request, cancellationToken: cancellationToken);

        return response.Connections.Select(c => new ConnectionInfoDto(
            c.ConnectionId,
            (SourceType)(int)c.Type,
            c.DisplayName,
            string.IsNullOrEmpty(c.ProviderAccountId) ? null : c.ProviderAccountId
        )).ToList();
    }

    public async Task<FileListResultDto> ListConnectionFilesAsync(
        string tenantId, SourceType sourceType, string? connectionId,
        string? path = null, string? pageToken = null, int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var request = new ListConnectionFilesRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            Type = sourceType,
            ConnectionId = connectionId ?? "",
            Path = path ?? "",
            PageToken = pageToken ?? "",
            PageSize = pageSize
        };

        var response = await GetSyncClient().ListConnectionFilesAsync(request, cancellationToken: cancellationToken);

        var files = response.Files.Select(f => new FileEntryDto(
            f.Name,
            f.Path,
            string.IsNullOrEmpty(f.FileId) ? null : f.FileId,
            f.IsFolder,
            f.SizeBytes,
            f.ModifiedAtUnix,
            string.IsNullOrEmpty(f.MimeType) ? null : f.MimeType
        )).ToList();

        return new FileListResultDto(
            files,
            string.IsNullOrEmpty(response.NextPageToken) ? null : response.NextPageToken
        );
    }

    public async Task<byte[]> DownloadFromSourceAsync(
        string tenantId, SourceType sourceType, string? connectionId,
        string path, string? fileId = null,
        CancellationToken cancellationToken = default)
    {
        var request = new DownloadFromSourceRequest
        {
            Context = new TenantContext { TenantId = tenantId },
            Type = sourceType,
            ConnectionId = connectionId ?? "",
            Path = path,
            FileId = fileId ?? ""
        };

        using var call = GetSyncClient().DownloadFromSource(request, cancellationToken: cancellationToken);

        var data = new MemoryStream();
        await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            if (chunk.Data.Length > 0)
                data.Write(chunk.Data.Span);

            if (chunk.IsLast)
                break;
        }

        return data.ToArray();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

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
