using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DocxMcp.Persistence;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Storage;

/// <summary>
/// Cloud-compatible session store using IStorageProvider.
/// Replaces memory-mapped files with cloud object storage (GCS, Azure, etc.).
/// Maintains API compatibility with the original SessionStore.
/// </summary>
public sealed class CloudSessionStore : IDisposable
{
    private readonly IStorageProvider _storage;
    private readonly ILogger<CloudSessionStore> _logger;
    private readonly ConcurrentDictionary<string, CloudWal> _openWals = new();
    private const string IndexFileName = "index.json";

    public CloudSessionStore(IStorageProvider storage, ILogger<CloudSessionStore> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public string ProviderType => _storage.ProviderType;

    public async Task EnsureDirectoryAsync(CancellationToken ct = default)
    {
        await _storage.EnsureDirectoryAsync("", ct);
    }

    // --- Cross-process distributed lock ---

    public async Task<IAsyncDisposable> AcquireLockAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        return await _storage.AcquireLockAsync("session-index", timeout ?? TimeSpan.FromSeconds(30), ct);
    }

    // --- Index operations ---

    public async Task<SessionIndexFile> LoadIndexAsync(CancellationToken ct = default)
    {
        try
        {
            if (!await _storage.ExistsAsync(IndexFileName, ct))
                return new SessionIndexFile();

            var bytes = await _storage.ReadAsync(IndexFileName, ct);
            var json = Encoding.UTF8.GetString(bytes);
            var index = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionIndexFile);

            if (index is null || index.Version != 1)
                return new SessionIndexFile();

            return index;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read session index; starting fresh.");
            return new SessionIndexFile();
        }
    }

    public async Task SaveIndexAsync(SessionIndexFile index, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(index, SessionJsonContext.Default.SessionIndexFile);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _storage.WriteAsync(IndexFileName, bytes, ct);
    }

    // --- Baseline operations ---

    public async Task PersistBaselineAsync(string sessionId, byte[] bytes, CancellationToken ct = default)
    {
        var path = BaselinePath(sessionId);
        await _storage.WriteAsync(path, bytes, ct);
    }

    public async Task<byte[]> LoadBaselineAsync(string sessionId, CancellationToken ct = default)
    {
        var path = BaselinePath(sessionId);
        return await _storage.ReadAsync(path, ct);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        // Close and remove the WAL cache first
        if (_openWals.TryRemove(sessionId, out var wal))
            await wal.DisposeAsync();

        await _storage.DeleteAsync(BaselinePath(sessionId), ct);
        await _storage.DeleteAsync(WalPath(sessionId), ct);
        await DeleteCheckpointsAsync(sessionId, ct);
    }

    // --- WAL operations ---

    public async Task<CloudWal> GetOrCreateWalAsync(string sessionId, CancellationToken ct = default)
    {
        if (_openWals.TryGetValue(sessionId, out var existing))
            return existing;

        var wal = new CloudWal(_storage, WalPath(sessionId), _logger);
        await wal.LoadAsync(ct);

        _openWals[sessionId] = wal;
        return wal;
    }

    public async Task AppendWalAsync(string sessionId, string patchesJson, string? description = null, CancellationToken ct = default)
    {
        var entry = new WalEntry
        {
            Patches = patchesJson,
            Timestamp = DateTime.UtcNow,
            Description = description
        };
        var line = JsonSerializer.Serialize(entry, WalJsonContext.Default.WalEntry);
        var wal = await GetOrCreateWalAsync(sessionId, ct);
        await wal.AppendAsync(line, ct);
    }

    public async Task<List<string>> ReadWalAsync(string sessionId, CancellationToken ct = default)
    {
        var wal = await GetOrCreateWalAsync(sessionId, ct);
        var patches = new List<string>();

        foreach (var line in wal.ReadAll())
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var entry = JsonSerializer.Deserialize(line, WalJsonContext.Default.WalEntry);
                if (entry?.Patches is not null)
                    patches.Add(entry.Patches);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping corrupt WAL line for session {SessionId}.", sessionId);
            }
        }

        return patches;
    }

    public async Task<List<string>> ReadWalRangeAsync(string sessionId, int from, int to, CancellationToken ct = default)
    {
        var wal = await GetOrCreateWalAsync(sessionId, ct);
        var lines = wal.ReadRange(from, to);
        var patches = new List<string>(lines.Count);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var entry = JsonSerializer.Deserialize(line, WalJsonContext.Default.WalEntry);
                if (entry?.Patches is not null)
                    patches.Add(entry.Patches);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping corrupt WAL line for session {SessionId}.", sessionId);
            }
        }

        return patches;
    }

    public async Task<List<WalEntry>> ReadWalEntriesAsync(string sessionId, CancellationToken ct = default)
    {
        var wal = await GetOrCreateWalAsync(sessionId, ct);
        var entries = new List<WalEntry>();

        foreach (var line in wal.ReadAll())
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                var entry = JsonSerializer.Deserialize(line, WalJsonContext.Default.WalEntry);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping corrupt WAL entry for session {SessionId}.", sessionId);
            }
        }

        return entries;
    }

    public async Task<int> WalEntryCountAsync(string sessionId, CancellationToken ct = default)
    {
        var wal = await GetOrCreateWalAsync(sessionId, ct);
        return wal.EntryCount;
    }

    public async Task TruncateWalAsync(string sessionId, CancellationToken ct = default)
    {
        var wal = await GetOrCreateWalAsync(sessionId, ct);
        await wal.TruncateAsync(ct);
    }

    public async Task TruncateWalAtAsync(string sessionId, int count, CancellationToken ct = default)
    {
        var wal = await GetOrCreateWalAsync(sessionId, ct);
        await wal.TruncateAtAsync(count, ct);
    }

    // --- Checkpoint operations ---

    public string CheckpointPath(string sessionId, int position) =>
        $"{sessionId}.ckpt.{position}.docx";

    public async Task PersistCheckpointAsync(string sessionId, int position, byte[] bytes, CancellationToken ct = default)
    {
        var path = CheckpointPath(sessionId, position);
        await _storage.WriteAsync(path, bytes, ct);
    }

    public async Task<(int position, byte[] bytes)> LoadNearestCheckpointAsync(
        string sessionId, int targetPosition, List<int> knownPositions, CancellationToken ct = default)
    {
        int bestPos = 0;
        foreach (var pos in knownPositions)
        {
            if (pos <= targetPosition && pos > bestPos)
                bestPos = pos;
        }

        if (bestPos > 0)
        {
            var path = CheckpointPath(sessionId, bestPos);
            if (await _storage.ExistsAsync(path, ct))
            {
                try
                {
                    var bytes = await _storage.ReadAsync(path, ct);
                    return (bestPos, bytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load checkpoint at position {Position} for session {SessionId}; falling back.",
                        bestPos, sessionId);
                }
            }
        }

        // Fallback to baseline
        return (0, await LoadBaselineAsync(sessionId, ct));
    }

    public async Task DeleteCheckpointsAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var prefix = $"{sessionId}.ckpt.";
            var files = await _storage.ListAsync(prefix, ct);
            foreach (var file in files)
            {
                await _storage.DeleteAsync(file, ct);
            }
        }
        catch { /* best effort */ }
    }

    public async Task DeleteCheckpointsAfterAsync(string sessionId, int afterPosition, List<int> knownPositions, CancellationToken ct = default)
    {
        foreach (var pos in knownPositions)
        {
            if (pos > afterPosition)
                await _storage.DeleteAsync(CheckpointPath(sessionId, pos), ct);
        }
    }

    // --- Path helpers ---

    public static string BaselinePath(string sessionId) => $"{sessionId}.docx";
    public static string WalPath(string sessionId) => $"{sessionId}.wal";

    public void Dispose()
    {
        foreach (var wal in _openWals.Values)
        {
            wal.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _openWals.Clear();
    }
}

/// <summary>
/// Cloud-based WAL implementation using IStorageProvider.
/// Caches entries in memory and persists to cloud storage.
/// </summary>
public sealed class CloudWal : IAsyncDisposable
{
    private readonly IStorageProvider _storage;
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly List<string> _entries = new();
    private bool _dirty;

    public CloudWal(IStorageProvider storage, string path, ILogger logger)
    {
        _storage = storage;
        _path = path;
        _logger = logger;
    }

    public int EntryCount => _entries.Count;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        try
        {
            if (await _storage.ExistsAsync(_path, ct))
            {
                var bytes = await _storage.ReadAsync(_path, ct);
                var content = Encoding.UTF8.GetString(bytes);
                var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                _entries.AddRange(lines);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load WAL from {Path}", _path);
        }
        _dirty = false;
    }

    public async Task AppendAsync(string line, CancellationToken ct = default)
    {
        _entries.Add(line);
        _dirty = true;
        await PersistAsync(ct);
    }

    public List<string> ReadAll() => _entries.ToList();

    public List<string> ReadRange(int from, int to)
    {
        var actualTo = Math.Min(to, _entries.Count);
        var actualFrom = Math.Max(from, 0);
        if (actualFrom >= actualTo)
            return new List<string>();
        return _entries.GetRange(actualFrom, actualTo - actualFrom);
    }

    public async Task TruncateAsync(CancellationToken ct = default)
    {
        _entries.Clear();
        _dirty = true;
        await PersistAsync(ct);
    }

    public async Task TruncateAtAsync(int count, CancellationToken ct = default)
    {
        if (count < _entries.Count)
        {
            _entries.RemoveRange(count, _entries.Count - count);
            _dirty = true;
            await PersistAsync(ct);
        }
    }

    private async Task PersistAsync(CancellationToken ct = default)
    {
        if (!_dirty) return;

        var content = string.Join("\n", _entries);
        var bytes = Encoding.UTF8.GetBytes(content);
        await _storage.WriteAsync(_path, bytes, ct);
        _dirty = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dirty)
        {
            await PersistAsync();
        }
    }
}
