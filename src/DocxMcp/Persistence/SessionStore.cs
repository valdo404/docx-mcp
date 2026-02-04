using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Persistence;

/// <summary>
/// Handles all disk I/O for session persistence using memory-mapped files.
/// Baselines are written via MemoryMappedFile (OS page cache handles flushing).
/// WAL files are kept mapped in memory for the lifetime of each session.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly string _sessionsDir;
    private readonly string _indexPath;
    private readonly string _lockPath;
    private readonly ILogger<SessionStore> _logger;
    private readonly ConcurrentDictionary<string, MappedWal> _openWals = new();

    public SessionStore(ILogger<SessionStore> logger, string? sessionsDir = null)
    {
        _logger = logger;
        _sessionsDir = sessionsDir
            ?? Environment.GetEnvironmentVariable("DOCX_SESSIONS_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "docx-mcp", "sessions");
        _indexPath = Path.Combine(_sessionsDir, "index.json");
        _lockPath = Path.Combine(_sessionsDir, ".lock");
    }

    public string SessionsDir => _sessionsDir;

    public void EnsureDirectory()
    {
        Directory.CreateDirectory(_sessionsDir);
    }

    // --- Cross-process file lock ---

    /// <summary>
    /// Acquire an exclusive file lock for cross-process index mutations.
    /// Uses exponential backoff with jitter on contention.
    /// </summary>
    public SessionLock AcquireLock(int maxRetries = 20, int initialDelayMs = 50)
    {
        EnsureDirectory();
        var delay = initialDelayMs;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var fs = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new SessionLock(fs);
            }
            catch (IOException) when (attempt < maxRetries)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 2000);
            }
        }

        throw new TimeoutException(
            $"Failed to acquire session lock after {maxRetries} retries ({_lockPath}).");
    }

    // --- Index operations ---

    public SessionIndexFile LoadIndex()
    {
        if (!File.Exists(_indexPath))
            return new SessionIndexFile();

        try
        {
            var json = File.ReadAllText(_indexPath);
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

    public void SaveIndex(SessionIndexFile index)
    {
        EnsureDirectory();
        var json = JsonSerializer.Serialize(index, SessionJsonContext.Default.SessionIndexFile);
        AtomicWrite(_indexPath, json);
    }

    // --- Baseline .docx operations (memory-mapped) ---

    /// <summary>
    /// Persist document bytes as a baseline snapshot via memory-mapped file.
    /// The write goes to the OS page cache; the kernel flushes to disk asynchronously.
    /// File format: [8 bytes: data length][docx bytes]
    /// </summary>
    public void PersistBaseline(string sessionId, byte[] bytes)
    {
        EnsureDirectory();
        var path = BaselinePath(sessionId);
        var capacity = bytes.Length + 8;

        // Ensure file exists with sufficient capacity
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fs.SetLength(capacity);
        fs.Close();

        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, capacity);
        using var accessor = mmf.CreateViewAccessor();
        accessor.Write(0, (long)bytes.Length);
        accessor.WriteArray(8, bytes, 0, bytes.Length);
        accessor.Flush();
    }

    /// <summary>
    /// Load baseline snapshot bytes from a memory-mapped file.
    /// </summary>
    public byte[] LoadBaseline(string sessionId)
    {
        var path = BaselinePath(sessionId);
        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var length = accessor.ReadInt64(0);
        if (length <= 0)
            throw new InvalidOperationException($"Baseline for session '{sessionId}' is empty or corrupt.");
        var bytes = new byte[length];
        accessor.ReadArray(8, bytes, 0, (int)length);
        return bytes;
    }

    public void DeleteSession(string sessionId)
    {
        // Close and remove the WAL mapping first
        if (_openWals.TryRemove(sessionId, out var wal))
            wal.Dispose();

        TryDelete(BaselinePath(sessionId));
        TryDelete(WalPath(sessionId));
        DeleteCheckpoints(sessionId);
    }

    // --- WAL operations (memory-mapped) ---

    /// <summary>
    /// Get or create a memory-mapped WAL for a session.
    /// The WAL stays mapped for the session's lifetime.
    /// </summary>
    public MappedWal GetOrCreateWal(string sessionId)
    {
        return _openWals.GetOrAdd(sessionId, id =>
        {
            EnsureDirectory();
            return new MappedWal(WalPath(id));
        });
    }

    public void AppendWal(string sessionId, string patchesJson)
    {
        var entry = new WalEntry
        {
            Patches = patchesJson,
            Timestamp = DateTime.UtcNow
        };
        var line = JsonSerializer.Serialize(entry, WalJsonContext.Default.WalEntry);
        GetOrCreateWal(sessionId).Append(line);
    }

    public void AppendWal(string sessionId, string patchesJson, string? description)
    {
        var entry = new WalEntry
        {
            Patches = patchesJson,
            Timestamp = DateTime.UtcNow,
            Description = description
        };
        var line = JsonSerializer.Serialize(entry, WalJsonContext.Default.WalEntry);
        GetOrCreateWal(sessionId).Append(line);
    }

    public List<string> ReadWal(string sessionId)
    {
        var wal = GetOrCreateWal(sessionId);
        wal.Refresh();
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

    /// <summary>
    /// Read WAL entries in range [from, to) as patch strings.
    /// </summary>
    public List<string> ReadWalRange(string sessionId, int from, int to)
    {
        var wal = GetOrCreateWal(sessionId);
        wal.Refresh();
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

    /// <summary>
    /// Read WAL entries with full metadata (timestamps, descriptions).
    /// </summary>
    public List<WalEntry> ReadWalEntries(string sessionId)
    {
        var wal = GetOrCreateWal(sessionId);
        wal.Refresh();
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

    public int WalEntryCount(string sessionId)
    {
        var wal = GetOrCreateWal(sessionId);
        wal.Refresh();
        return wal.EntryCount;
    }

    public void TruncateWal(string sessionId)
    {
        GetOrCreateWal(sessionId).Truncate();
    }

    /// <summary>
    /// Keep first <paramref name="count"/> WAL entries, discard the rest.
    /// </summary>
    public void TruncateWalAt(string sessionId, int count)
    {
        GetOrCreateWal(sessionId).TruncateAt(count);
    }

    // --- Checkpoint operations ---

    public string CheckpointPath(string sessionId, int position) =>
        Path.Combine(_sessionsDir, $"{sessionId}.ckpt.{position}.docx");

    public string ImportCheckpointPath(string sessionId, int position) =>
        Path.Combine(_sessionsDir, $"{sessionId}.import.{position}.docx");

    /// <summary>
    /// Persist a checkpoint snapshot at the given WAL position.
    /// Same memory-mapped format as baseline.
    /// </summary>
    public void PersistCheckpoint(string sessionId, int position, byte[] bytes)
    {
        PersistCheckpointToPath(CheckpointPath(sessionId, position), bytes);
    }

    /// <summary>
    /// Persist an import checkpoint snapshot at the given WAL position.
    /// Uses the import checkpoint naming convention ({sessionId}.import.{position}.docx).
    /// </summary>
    public void PersistImportCheckpoint(string sessionId, int position, byte[] bytes)
    {
        PersistCheckpointToPath(ImportCheckpointPath(sessionId, position), bytes);
    }

    private void PersistCheckpointToPath(string path, byte[] bytes)
    {
        EnsureDirectory();
        var capacity = bytes.Length + 8;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fs.SetLength(capacity);
        fs.Close();

        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, capacity);
        using var accessor = mmf.CreateViewAccessor();
        accessor.Write(0, (long)bytes.Length);
        accessor.WriteArray(8, bytes, 0, bytes.Length);
        accessor.Flush();
    }

    /// <summary>
    /// Load the nearest checkpoint at or before targetPosition.
    /// Falls back to baseline (position 0) if no checkpoint qualifies.
    /// </summary>
    public (int position, byte[] bytes) LoadNearestCheckpoint(string sessionId, int targetPosition, List<int> knownPositions)
    {
        // Find the largest checkpoint position <= targetPosition
        int bestPos = 0;
        foreach (var pos in knownPositions)
        {
            if (pos <= targetPosition && pos > bestPos)
                bestPos = pos;
        }

        if (bestPos > 0)
        {
            // Check import checkpoint first (takes precedence), then regular checkpoint
            var importPath = ImportCheckpointPath(sessionId, bestPos);
            var ckptPath = CheckpointPath(sessionId, bestPos);
            var path = File.Exists(importPath) ? importPath : ckptPath;

            if (File.Exists(path))
            {
                try
                {
                    using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                    using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    var length = accessor.ReadInt64(0);
                    if (length > 0)
                    {
                        var bytes = new byte[length];
                        accessor.ReadArray(8, bytes, 0, (int)length);
                        return (bestPos, bytes);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load checkpoint at position {Position} for session {SessionId}; falling back.",
                        bestPos, sessionId);
                }
            }
        }

        // Fallback to baseline
        return (0, LoadBaseline(sessionId));
    }

    /// <summary>
    /// Delete all checkpoint files for a session.
    /// </summary>
    public void DeleteCheckpoints(string sessionId)
    {
        try
        {
            var dir = new DirectoryInfo(_sessionsDir);
            if (!dir.Exists) return;

            foreach (var file in dir.GetFiles($"{sessionId}.ckpt.*.docx"))
                TryDelete(file.FullName);
            foreach (var file in dir.GetFiles($"{sessionId}.import.*.docx"))
                TryDelete(file.FullName);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Delete checkpoint files for positions strictly greater than afterPosition.
    /// </summary>
    public void DeleteCheckpointsAfter(string sessionId, int afterPosition, List<int> knownPositions)
    {
        foreach (var pos in knownPositions)
        {
            if (pos > afterPosition)
            {
                TryDelete(CheckpointPath(sessionId, pos));
                TryDelete(ImportCheckpointPath(sessionId, pos));
            }
        }
    }

    // --- Path helpers ---

    public string BaselinePath(string sessionId) =>
        Path.Combine(_sessionsDir, $"{sessionId}.docx");

    public string WalPath(string sessionId) =>
        Path.Combine(_sessionsDir, $"{sessionId}.wal");

    private void AtomicWrite(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        foreach (var wal in _openWals.Values)
            wal.Dispose();
        _openWals.Clear();
    }
}
