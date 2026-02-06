namespace DocxMcp.Grpc;

/// <summary>
/// Interface for storage client operations.
/// Allows for mocking in tests.
/// </summary>
public interface IStorageClient : IAsyncDisposable
{
    // Session operations
    Task<(byte[]? Data, bool Found)> LoadSessionAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    Task SaveSessionAsync(
        string tenantId, string sessionId, byte[] data, CancellationToken cancellationToken = default);

    Task<bool> DeleteSessionAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    Task<bool> SessionExistsAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionInfoDto>> ListSessionsAsync(
        string tenantId, CancellationToken cancellationToken = default);

    // Index operations (atomic - server handles locking internally)
    Task<(byte[]? Data, bool Found)> LoadIndexAsync(
        string tenantId, CancellationToken cancellationToken = default);

    Task<(bool Success, bool AlreadyExists)> AddSessionToIndexAsync(
        string tenantId, string sessionId, SessionIndexEntryDto entry,
        CancellationToken cancellationToken = default);

    Task<(bool Success, bool NotFound)> UpdateSessionInIndexAsync(
        string tenantId, string sessionId,
        long? modifiedAtUnix = null, ulong? walPosition = null,
        IEnumerable<ulong>? addCheckpointPositions = null,
        IEnumerable<ulong>? removeCheckpointPositions = null,
        CancellationToken cancellationToken = default);

    Task<(bool Success, bool Existed)> RemoveSessionFromIndexAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    // WAL operations
    Task<ulong> AppendWalAsync(
        string tenantId, string sessionId, IEnumerable<WalEntryDto> entries,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<WalEntryDto> Entries, bool HasMore)> ReadWalAsync(
        string tenantId, string sessionId, ulong fromPosition = 0, ulong limit = 0,
        CancellationToken cancellationToken = default);

    Task<ulong> TruncateWalAsync(
        string tenantId, string sessionId, ulong keepFromPosition,
        CancellationToken cancellationToken = default);

    // Checkpoint operations
    Task SaveCheckpointAsync(
        string tenantId, string sessionId, ulong position, byte[] data,
        CancellationToken cancellationToken = default);

    Task<(byte[]? Data, ulong Position, bool Found)> LoadCheckpointAsync(
        string tenantId, string sessionId, ulong position = 0,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CheckpointInfoDto>> ListCheckpointsAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    // Health check
    Task<(bool Healthy, string Backend, string Version)> HealthCheckAsync(
        CancellationToken cancellationToken = default);

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
