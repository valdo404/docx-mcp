namespace DocxMcp.Grpc;

/// <summary>
/// Interface for sync storage operations (source sync + external watch).
/// Maps to the SourceSyncService and ExternalWatchService gRPC services.
/// </summary>
public interface ISyncStorage : IAsyncDisposable
{
    // SourceSync operations
    Task<(bool Success, string Error)> RegisterSourceAsync(
        string tenantId, string sessionId, SourceType sourceType, string uri, bool autoSync,
        CancellationToken cancellationToken = default);

    Task<bool> UnregisterSourceAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    Task<(bool Success, string Error)> UpdateSourceAsync(
        string tenantId, string sessionId,
        SourceType? sourceType = null, string? uri = null, bool? autoSync = null,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Error, long SyncedAtUnix)> SyncToSourceAsync(
        string tenantId, string sessionId, byte[] data,
        CancellationToken cancellationToken = default);

    Task<SyncStatusDto?> GetSyncStatusAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    // ExternalWatch operations
    Task<(bool Success, string WatchId, string Error)> StartWatchAsync(
        string tenantId, string sessionId, SourceType sourceType, string uri, int pollIntervalSeconds = 0,
        CancellationToken cancellationToken = default);

    Task<bool> StopWatchAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    Task<(bool HasChanges, SourceMetadataDto? Current, SourceMetadataDto? Known)> CheckForChangesAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    Task<SourceMetadataDto?> GetSourceMetadataAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribe to external change events for specified sessions.
    /// Returns an IAsyncEnumerable that yields events as they occur.
    /// </summary>
    IAsyncEnumerable<ExternalChangeEventDto> WatchChangesAsync(
        string tenantId, IEnumerable<string> sessionIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sync status DTO.
/// </summary>
public record SyncStatusDto(
    string SessionId,
    SourceType SourceType,
    string Uri,
    bool AutoSyncEnabled,
    long? LastSyncedAtUnix,
    bool HasPendingChanges,
    string? LastError);

/// <summary>
/// Source metadata DTO.
/// </summary>
public record SourceMetadataDto(
    long SizeBytes,
    long ModifiedAtUnix,
    string? Etag,
    string? VersionId,
    byte[]? ContentHash);

// Note: ExternalChangeType is generated from proto/storage.proto

/// <summary>
/// External change event DTO.
/// </summary>
public record ExternalChangeEventDto(
    string SessionId,
    ExternalChangeType ChangeType,
    SourceMetadataDto? OldMetadata,
    SourceMetadataDto? NewMetadata,
    long DetectedAtUnix,
    string? NewUri);
