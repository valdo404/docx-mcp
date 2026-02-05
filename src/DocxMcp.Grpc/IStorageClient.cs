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

    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(
        string tenantId, CancellationToken cancellationToken = default);

    // Index operations
    Task<(byte[]? Data, bool Found)> LoadIndexAsync(
        string tenantId, CancellationToken cancellationToken = default);

    Task SaveIndexAsync(
        string tenantId, byte[] indexJson, CancellationToken cancellationToken = default);

    // WAL operations
    Task<ulong> AppendWalAsync(
        string tenantId, string sessionId, IEnumerable<WalEntry> entries,
        CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<WalEntry> Entries, bool HasMore)> ReadWalAsync(
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

    Task<IReadOnlyList<CheckpointInfo>> ListCheckpointsAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default);

    // Lock operations
    Task<(bool Acquired, string? CurrentHolder, long ExpiresAt)> AcquireLockAsync(
        string tenantId, string resourceId, string holderId, int ttlSeconds = 60,
        CancellationToken cancellationToken = default);

    Task<(bool Released, string Reason)> ReleaseLockAsync(
        string tenantId, string resourceId, string holderId,
        CancellationToken cancellationToken = default);

    Task<(bool Renewed, long ExpiresAt, string Reason)> RenewLockAsync(
        string tenantId, string resourceId, string holderId, int ttlSeconds = 60,
        CancellationToken cancellationToken = default);

    // Health check
    Task<(bool Healthy, string Backend, string Version)> HealthCheckAsync(
        CancellationToken cancellationToken = default);
}
