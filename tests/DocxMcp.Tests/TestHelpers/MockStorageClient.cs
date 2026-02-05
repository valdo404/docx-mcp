using System.Collections.Concurrent;
using DocxMcp.Grpc;

namespace DocxMcp.Tests.TestHelpers;

/// <summary>
/// In-memory mock implementation of IStorageClient for testing.
/// Simulates the gRPC storage service without requiring a real server.
/// </summary>
public sealed class MockStorageClient : IStorageClient
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> _sessions = new();
    private readonly ConcurrentDictionary<string, byte[]> _indexes = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, List<WalEntry>>> _wals = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte[]>>> _checkpoints = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (string HolderId, DateTime ExpiresAt)>> _locks = new();

    private ConcurrentDictionary<string, byte[]> GetTenantSessions(string tenantId)
        => _sessions.GetOrAdd(tenantId, _ => new());

    private ConcurrentDictionary<string, List<WalEntry>> GetTenantWals(string tenantId)
        => _wals.GetOrAdd(tenantId, _ => new());

    private ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte[]>> GetTenantCheckpoints(string tenantId)
        => _checkpoints.GetOrAdd(tenantId, _ => new());

    private ConcurrentDictionary<string, (string HolderId, DateTime ExpiresAt)> GetTenantLocks(string tenantId)
        => _locks.GetOrAdd(tenantId, _ => new());

    // Session operations

    public Task<(byte[]? Data, bool Found)> LoadSessionAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sessions = GetTenantSessions(tenantId);
        if (sessions.TryGetValue(sessionId, out var data))
            return Task.FromResult<(byte[]?, bool)>((data, true));
        return Task.FromResult<(byte[]?, bool)>((null, false));
    }

    public Task SaveSessionAsync(
        string tenantId, string sessionId, byte[] data, CancellationToken cancellationToken = default)
    {
        var sessions = GetTenantSessions(tenantId);
        sessions[sessionId] = data;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteSessionAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sessions = GetTenantSessions(tenantId);
        var existed = sessions.TryRemove(sessionId, out _);

        // Also delete WAL and checkpoints
        var wals = GetTenantWals(tenantId);
        wals.TryRemove(sessionId, out _);

        var checkpoints = GetTenantCheckpoints(tenantId);
        checkpoints.TryRemove(sessionId, out _);

        return Task.FromResult(existed);
    }

    public Task<bool> SessionExistsAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var sessions = GetTenantSessions(tenantId);
        return Task.FromResult(sessions.ContainsKey(sessionId));
    }

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var sessions = GetTenantSessions(tenantId);
        var result = sessions.Select(kvp => new SessionInfo
        {
            SessionId = kvp.Key,
            SourcePath = "",
            CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ModifiedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SizeBytes = kvp.Value.Length
        }).ToList();

        return Task.FromResult<IReadOnlyList<SessionInfo>>(result);
    }

    // Index operations

    public Task<(byte[]? Data, bool Found)> LoadIndexAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        if (_indexes.TryGetValue(tenantId, out var data))
            return Task.FromResult<(byte[]?, bool)>((data, true));
        return Task.FromResult<(byte[]?, bool)>((null, false));
    }

    public Task SaveIndexAsync(
        string tenantId, byte[] indexJson, CancellationToken cancellationToken = default)
    {
        _indexes[tenantId] = indexJson;
        return Task.CompletedTask;
    }

    // WAL operations

    public Task<ulong> AppendWalAsync(
        string tenantId, string sessionId, IEnumerable<WalEntry> entries, CancellationToken cancellationToken = default)
    {
        var wals = GetTenantWals(tenantId);
        var wal = wals.GetOrAdd(sessionId, _ => new List<WalEntry>());

        lock (wal)
        {
            foreach (var entry in entries)
            {
                var newEntry = new WalEntry
                {
                    Position = (ulong)wal.Count,
                    Operation = entry.Operation,
                    Path = entry.Path,
                    PatchJson = entry.PatchJson,
                    TimestampUnix = entry.TimestampUnix
                };
                wal.Add(newEntry);
            }
            return Task.FromResult((ulong)wal.Count);
        }
    }

    public Task<(IReadOnlyList<WalEntry> Entries, bool HasMore)> ReadWalAsync(
        string tenantId, string sessionId, ulong fromPosition = 0, ulong limit = 0,
        CancellationToken cancellationToken = default)
    {
        var wals = GetTenantWals(tenantId);
        if (!wals.TryGetValue(sessionId, out var wal))
            return Task.FromResult<(IReadOnlyList<WalEntry>, bool)>((Array.Empty<WalEntry>(), false));

        lock (wal)
        {
            var entries = wal.Skip((int)fromPosition);
            if (limit > 0)
                entries = entries.Take((int)limit);

            var result = entries.ToList();
            var hasMore = limit > 0 && fromPosition + limit < (ulong)wal.Count;
            return Task.FromResult<(IReadOnlyList<WalEntry>, bool)>((result, hasMore));
        }
    }

    public Task<ulong> TruncateWalAsync(
        string tenantId, string sessionId, ulong keepFromPosition, CancellationToken cancellationToken = default)
    {
        var wals = GetTenantWals(tenantId);
        if (!wals.TryGetValue(sessionId, out var wal))
            return Task.FromResult(0UL);

        lock (wal)
        {
            var removed = wal.Count - (int)keepFromPosition;
            if (removed > 0)
            {
                wal.RemoveRange((int)keepFromPosition, removed);
            }
            return Task.FromResult((ulong)Math.Max(0, removed));
        }
    }

    // Checkpoint operations

    public Task SaveCheckpointAsync(
        string tenantId, string sessionId, ulong position, byte[] data,
        CancellationToken cancellationToken = default)
    {
        var tenantCheckpoints = GetTenantCheckpoints(tenantId);
        var sessionCheckpoints = tenantCheckpoints.GetOrAdd(sessionId, _ => new());
        sessionCheckpoints[position] = data;
        return Task.CompletedTask;
    }

    public Task<(byte[]? Data, ulong Position, bool Found)> LoadCheckpointAsync(
        string tenantId, string sessionId, ulong position = 0, CancellationToken cancellationToken = default)
    {
        var tenantCheckpoints = GetTenantCheckpoints(tenantId);
        if (!tenantCheckpoints.TryGetValue(sessionId, out var sessionCheckpoints))
            return Task.FromResult<(byte[]?, ulong, bool)>((null, 0, false));

        if (position == 0)
        {
            // Get latest checkpoint
            var latest = sessionCheckpoints.Keys.DefaultIfEmpty().Max();
            if (latest > 0 && sessionCheckpoints.TryGetValue(latest, out var latestData))
                return Task.FromResult<(byte[]?, ulong, bool)>((latestData, latest, true));
            return Task.FromResult<(byte[]?, ulong, bool)>((null, 0, false));
        }

        // Find nearest checkpoint at or before position
        var nearest = sessionCheckpoints.Keys
            .Where(p => p <= position)
            .DefaultIfEmpty()
            .Max();

        if (nearest > 0 && sessionCheckpoints.TryGetValue(nearest, out var data))
            return Task.FromResult<(byte[]?, ulong, bool)>((data, nearest, true));

        return Task.FromResult<(byte[]?, ulong, bool)>((null, 0, false));
    }

    public Task<IReadOnlyList<CheckpointInfo>> ListCheckpointsAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var tenantCheckpoints = GetTenantCheckpoints(tenantId);
        if (!tenantCheckpoints.TryGetValue(sessionId, out var sessionCheckpoints))
            return Task.FromResult<IReadOnlyList<CheckpointInfo>>(Array.Empty<CheckpointInfo>());

        var result = sessionCheckpoints.Select(kvp => new CheckpointInfo
        {
            Position = kvp.Key,
            CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            SizeBytes = kvp.Value.Length
        }).ToList();

        return Task.FromResult<IReadOnlyList<CheckpointInfo>>(result);
    }

    // Lock operations

    public Task<(bool Acquired, string? CurrentHolder, long ExpiresAt)> AcquireLockAsync(
        string tenantId, string resourceId, string holderId, int ttlSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var locks = GetTenantLocks(tenantId);
        var expiresAt = DateTime.UtcNow.AddSeconds(ttlSeconds);

        // Check if lock exists and is not expired
        if (locks.TryGetValue(resourceId, out var existing))
        {
            if (existing.ExpiresAt > DateTime.UtcNow && existing.HolderId != holderId)
            {
                return Task.FromResult((false, (string?)existing.HolderId, new DateTimeOffset(existing.ExpiresAt).ToUnixTimeSeconds()));
            }
        }

        locks[resourceId] = (holderId, expiresAt);
        return Task.FromResult((true, (string?)null, new DateTimeOffset(expiresAt).ToUnixTimeSeconds()));
    }

    public Task<(bool Released, string Reason)> ReleaseLockAsync(
        string tenantId, string resourceId, string holderId, CancellationToken cancellationToken = default)
    {
        var locks = GetTenantLocks(tenantId);

        if (!locks.TryGetValue(resourceId, out var existing))
            return Task.FromResult((false, "not_found"));

        if (existing.HolderId != holderId)
            return Task.FromResult((false, "not_owner"));

        locks.TryRemove(resourceId, out _);
        return Task.FromResult((true, "ok"));
    }

    public Task<(bool Renewed, long ExpiresAt, string Reason)> RenewLockAsync(
        string tenantId, string resourceId, string holderId, int ttlSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        var locks = GetTenantLocks(tenantId);

        if (!locks.TryGetValue(resourceId, out var existing))
            return Task.FromResult((false, 0L, "not_found"));

        if (existing.HolderId != holderId)
            return Task.FromResult((false, 0L, "not_owner"));

        var expiresAt = DateTime.UtcNow.AddSeconds(ttlSeconds);
        locks[resourceId] = (holderId, expiresAt);
        return Task.FromResult((true, new DateTimeOffset(expiresAt).ToUnixTimeSeconds(), "ok"));
    }

    // Health check

    public Task<(bool Healthy, string Backend, string Version)> HealthCheckAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult((true, "mock", "1.0.0"));
    }

    // Test helpers

    public int GetWalEntryCount(string tenantId, string sessionId)
    {
        var wals = GetTenantWals(tenantId);
        return wals.TryGetValue(sessionId, out var wal) ? wal.Count : 0;
    }

    public bool CheckpointExists(string tenantId, string sessionId, ulong position)
    {
        var tenantCheckpoints = GetTenantCheckpoints(tenantId);
        return tenantCheckpoints.TryGetValue(sessionId, out var sessionCheckpoints)
            && sessionCheckpoints.ContainsKey(position);
    }

    public void Clear()
    {
        _sessions.Clear();
        _indexes.Clear();
        _wals.Clear();
        _checkpoints.Clear();
        _locks.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Clear();
        return ValueTask.CompletedTask;
    }
}
