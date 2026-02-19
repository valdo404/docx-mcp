using System.Text.Json;
using DocxMcp.Grpc;
using DocxMcp.Persistence;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

using GrpcWalEntry = DocxMcp.Grpc.WalEntryDto;
using WalEntry = DocxMcp.Persistence.WalEntry;

namespace DocxMcp;

/// <summary>
/// Thread-safe manager for document sessions with gRPC-based persistence.
/// Sessions are stored via a gRPC history storage service with multi-tenant isolation.
/// Supports undo/redo via WAL cursor + checkpoint replay.
/// Sync and watch operations are handled separately by SyncManager.
/// </summary>
public sealed class SessionManager
{
    private readonly IHistoryStorage _history;
    private readonly ILogger<SessionManager> _logger;
    private readonly string _tenantId;
    private readonly int _compactThreshold;

    /// <summary>
    /// The tenant ID for this SessionManager instance.
    /// Captured at construction time to ensure consistency across threads.
    /// </summary>
    public string TenantId => _tenantId;

    /// <summary>
    /// Create a SessionManager with the specified tenant ID.
    /// If tenantId is null, uses the current tenant from TenantContextHelper.
    /// </summary>
    public SessionManager(IHistoryStorage history, ILogger<SessionManager> logger, string? tenantId = null)
    {
        _history = history;
        _logger = logger;
        _tenantId = tenantId ?? TenantContextHelper.CurrentTenantId;

        var thresholdEnv = Environment.GetEnvironmentVariable("DOCX_WAL_COMPACT_THRESHOLD");
        _compactThreshold = int.TryParse(thresholdEnv, out var t) && t > 0 ? t : 50;
    }

    public DocxSession Open(string path)
    {
        var session = DocxSession.Open(path);
        try
        {
            PersistNewSessionAsync(session).GetAwaiter().GetResult();
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    public DocxSession OpenFromBytes(byte[] data, string? displayPath = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var session = DocxSession.FromBytes(data, id, displayPath);
        try
        {
            PersistNewSessionAsync(session).GetAwaiter().GetResult();
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    public DocxSession Create()
    {
        var session = DocxSession.Create();
        try
        {
            PersistNewSessionAsync(session).GetAwaiter().GetResult();
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    /// <summary>
    /// Load a session from gRPC checkpoint (stateless).
    /// Fast path: exact checkpoint at cursor position (1 gRPC call).
    /// Slow path: nearest checkpoint + WAL replay.
    /// The caller MUST dispose the returned session.
    /// </summary>
    public DocxSession Get(string id)
    {
        var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
        var cursor = LoadCursorPosition(id, walCount);

        // Try to load checkpoint at or before cursor position
        var (ckptData, ckptPos, ckptFound) = _history.LoadCheckpointAsync(
            TenantId, id, (ulong)cursor).GetAwaiter().GetResult();

        byte[] baseBytes;
        int checkpointPosition;

        if (ckptFound && ckptData is not null && (int)ckptPos <= cursor)
        {
            baseBytes = ckptData;
            checkpointPosition = (int)ckptPos;
        }
        else
        {
            // Fallback to baseline
            var (baselineData, baselineFound) = _history.LoadSessionAsync(TenantId, id)
                .GetAwaiter().GetResult();
            if (!baselineFound || baselineData is null)
                throw new KeyNotFoundException($"No document session with ID '{id}'.");
            baseBytes = baselineData;
            checkpointPosition = 0;
        }

        var sourcePath = LoadSourcePath(id);
        var session = DocxSession.FromBytes(baseBytes, id, sourcePath);

        // Replay WAL entries from checkpoint to cursor if needed
        if (cursor > checkpointPosition)
        {
            var walEntries = ReadWalEntriesAsync(id).GetAwaiter().GetResult();
            foreach (var patchJson in walEntries
                .Skip(checkpointPosition)
                .Take(cursor - checkpointPosition)
                .Where(e => e.Patches is not null)
                .Select(e => e.Patches!))
            {
                try { ReplayPatch(session, patchJson); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to replay WAL entry for session {SessionId}.", id);
                    break;
                }
            }
        }

        return session;
    }

    /// <summary>
    /// Resolve a session by ID or file path.
    /// - If the input matches a session ID in the index, loads that session.
    /// - If the input is a file path, checks the index for a session with that source_path.
    /// - If no existing session found and file exists, auto-opens a new session.
    /// </summary>
    public DocxSession ResolveSession(string idOrPath)
    {
        // First, try as session ID via gRPC
        var (exists, _) = _history.SessionExistsAsync(TenantId, idOrPath).GetAwaiter().GetResult();
        if (exists)
            return Get(idOrPath);

        // Check if it looks like a file path
        var isLikelyPath = idOrPath.Contains(Path.DirectorySeparatorChar)
            || idOrPath.Contains(Path.AltDirectorySeparatorChar)
            || idOrPath.StartsWith('~')
            || idOrPath.StartsWith('.')
            || Path.HasExtension(idOrPath);

        if (!isLikelyPath)
        {
            throw new KeyNotFoundException($"No document session with ID '{idOrPath}'.");
        }

        // Expand ~ to home directory
        var expandedPath = idOrPath;
        if (expandedPath.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expandedPath = Path.Combine(home, expandedPath[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var absolutePath = Path.GetFullPath(expandedPath);

        // Search the gRPC index for a session with this source_path
        var sessionList = List();
        var existingId = sessionList.FirstOrDefault(s =>
            s.Path is not null &&
            string.Equals(s.Path, absolutePath, StringComparison.OrdinalIgnoreCase)).Id;

        if (existingId is not null)
            return Get(existingId);

        // Auto-open if file exists
        if (File.Exists(absolutePath))
            return Open(absolutePath);

        throw new KeyNotFoundException($"No session found for '{idOrPath}' and file does not exist.");
    }

    /// <summary>
    /// Persist source path to the gRPC index and update the in-memory session.
    /// </summary>
    public void SetSourcePath(string id, string path)
    {
        var absolutePath = Path.GetFullPath(path);

        // Persist to gRPC index (stateless — no in-memory session to update)
        _history.UpdateSessionInIndexAsync(TenantId, id, sourcePath: absolutePath)
            .GetAwaiter().GetResult();
    }

    public void Close(string id)
    {
        // Verify session exists in the index before deleting
        var (exists, _) = _history.SessionExistsAsync(TenantId, id).GetAwaiter().GetResult();
        if (!exists)
            throw new KeyNotFoundException($"No document session with ID '{id}'.");

        _history.DeleteSessionAsync(TenantId, id).GetAwaiter().GetResult();
        _history.RemoveSessionFromIndexAsync(TenantId, id).GetAwaiter().GetResult();
    }

    public IReadOnlyList<(string Id, string? Path)> List()
    {
        var (indexData, found) = _history.LoadIndexAsync(TenantId).GetAwaiter().GetResult();
        if (!found || indexData is null)
            return Array.Empty<(string, string?)>();

        var json = System.Text.Encoding.UTF8.GetString(indexData);
        var index = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionIndex);
        if (index is null)
            return Array.Empty<(string, string?)>();

        return index.Sessions
            .Select(e => (e.Id, (string?)e.SourcePath))
            .ToList()
            .AsReadOnly();
    }

    // --- WAL operations ---

    /// <summary>
    /// Append a patch to the WAL after a successful mutation.
    /// If the cursor is behind the WAL tip (after undo), truncates future entries first.
    /// Always saves a checkpoint at the new position for stateless Get().
    /// Does NOT auto-save — caller is responsible for orchestrating sync.
    /// </summary>
    public void AppendWal(string id, string patchesJson, string? description, byte[] currentBytes)
    {
        try
        {
            var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
            var cursor = LoadCursorPosition(id, walCount);

            // If cursor < walCount, we're in an undo state — truncate future
            if (cursor < walCount)
            {
                TruncateWalAtAsync(id, cursor).GetAwaiter().GetResult();

                // Remove checkpoints above cursor position
                var checkpointsToRemove = GetCheckpointPositionsAboveAsync(id, (ulong)cursor).GetAwaiter().GetResult();
                if (checkpointsToRemove.Count > 0)
                {
                    _history.UpdateSessionInIndexAsync(TenantId, id,
                        removeCheckpointPositions: checkpointsToRemove).GetAwaiter().GetResult();
                }
            }

            // Auto-generate description from patch ops if not provided
            description ??= GenerateDescription(patchesJson);

            // Create WAL entry
            var walEntry = new WalEntry
            {
                Patches = patchesJson,
                Timestamp = DateTime.UtcNow,
                Description = description
            };

            AppendWalEntryAsync(id, walEntry).GetAwaiter().GetResult();
            var newCursor = cursor + 1;

            // Always save checkpoint at the new position (stateless pattern)
            _history.SaveCheckpointAsync(TenantId, id, (ulong)newCursor, currentBytes)
                .GetAwaiter().GetResult();

            // Update index with new WAL position, cursor, and checkpoint
            var newWalCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _history.UpdateSessionInIndexAsync(TenantId, id,
                modifiedAtUnix: now,
                walPosition: (ulong)newWalCount,
                cursorPosition: (ulong)newCursor,
                addCheckpointPositions: new[] { (ulong)newCursor }).GetAwaiter().GetResult();

            // Check if compaction is needed
            if ((ulong)newWalCount >= (ulong)_compactThreshold)
                Compact(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append WAL for session {SessionId}.", id);
            throw new McpException($"Failed to persist edit for session '{id}': {ex.Message}. The in-memory document was modified but the change was not saved to the write-ahead log.", ex);
        }
    }

    private async Task<List<ulong>> GetCheckpointPositionsAboveAsync(string id, ulong threshold)
    {
        var (indexData, found) = await _history.LoadIndexAsync(TenantId);
        if (!found || indexData is null)
            return new List<ulong>();

        var json = System.Text.Encoding.UTF8.GetString(indexData);
        var index = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionIndex);
        if (index is null || !index.TryGetValue(id, out var entry))
            return new List<ulong>();

        return entry!.CheckpointPositions.Where(p => (ulong)p > threshold).Select(p => (ulong)p).ToList();
    }

    private async Task<List<int>> GetCheckpointPositionsAsync(string id)
    {
        var (indexData, found) = await _history.LoadIndexAsync(TenantId);
        if (!found || indexData is null)
            return new List<int>();

        var json = System.Text.Encoding.UTF8.GetString(indexData);
        var index = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionIndex);
        if (index is null || !index.TryGetValue(id, out var entry))
            return new List<int>();

        return entry!.CheckpointPositions;
    }

    /// <summary>
    /// Create a new baseline snapshot from the current in-memory state and truncate the WAL.
    /// Refuses if redo entries exist unless discardRedoHistory is true.
    /// </summary>
    public void Compact(string id, bool discardRedoHistory = false)
    {
        try
        {
            var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
            var cursor = LoadCursorPosition(id, walCount);

            if (cursor < walCount && !discardRedoHistory)
            {
                _logger.LogInformation(
                    "Skipping compaction for session {SessionId}: {RedoCount} redo entries exist.",
                    id, walCount - cursor);
                return;
            }

            // Load current state from checkpoint
            using var session = Get(id);
            var bytes = session.ToBytes();

            _history.SaveSessionAsync(TenantId, id, bytes).GetAwaiter().GetResult();
            _history.TruncateWalAsync(TenantId, id, 0).GetAwaiter().GetResult();

            // Remove all checkpoints (baseline is now up-to-date)
            var checkpointsToRemove = GetCheckpointPositionsAboveAsync(id, 0).GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _history.UpdateSessionInIndexAsync(TenantId, id,
                modifiedAtUnix: now,
                walPosition: 0,
                cursorPosition: 0,
                removeCheckpointPositions: checkpointsToRemove).GetAwaiter().GetResult();

            _logger.LogInformation("Compacted session {SessionId}.", id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compact session {SessionId}.", id);
        }
    }

    /// <summary>
    /// Append an external sync entry to the WAL.
    /// </summary>
    public int AppendExternalSync(string id, WalEntry syncEntry, byte[] newBytes)
    {
        try
        {
            var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
            var cursor = LoadCursorPosition(id, walCount);

            // If cursor < walCount, we're in an undo state — truncate future
            if (cursor < walCount)
            {
                TruncateWalAtAsync(id, cursor).GetAwaiter().GetResult();

                // Remove checkpoints above cursor position
                var checkpointsToRemove = GetCheckpointPositionsAboveAsync(id, (ulong)cursor).GetAwaiter().GetResult();
                if (checkpointsToRemove.Count > 0)
                {
                    _history.UpdateSessionInIndexAsync(TenantId, id,
                        removeCheckpointPositions: checkpointsToRemove).GetAwaiter().GetResult();
                }
            }

            AppendWalEntryAsync(id, syncEntry).GetAwaiter().GetResult();

            var newCursor = cursor + 1;

            // Always save checkpoint with the new document bytes
            var checkpointBytes = syncEntry.SyncMeta?.DocumentSnapshot ?? newBytes;
            _history.SaveCheckpointAsync(TenantId, id, (ulong)newCursor, checkpointBytes)
                .GetAwaiter().GetResult();

            // Update index with new WAL position and checkpoint
            var newWalCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _history.UpdateSessionInIndexAsync(TenantId, id,
                modifiedAtUnix: now,
                walPosition: (ulong)newWalCount,
                cursorPosition: (ulong)newCursor,
                addCheckpointPositions: new[] { (ulong)newCursor }).GetAwaiter().GetResult();

            _logger.LogInformation("Appended external sync entry at position {Position} for session {SessionId}.",
                newCursor, id);

            return newCursor;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append external sync for session {SessionId}.", id);
            throw;
        }
    }

    // --- Undo / Redo / JumpTo / History ---

    public UndoRedoResult Undo(string id, int steps = 1)
    {
        var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
        var cursor = LoadCursorPosition(id, walCount);

        if (cursor <= 0)
            return new UndoRedoResult { Position = 0, Steps = 0, Message = "Already at the beginning. Nothing to undo." };

        var actualSteps = Math.Min(steps, cursor);
        var newCursor = cursor - actualSteps;

        var bytes = RebuildAndCheckpoint(id, newCursor);

        return new UndoRedoResult
        {
            Position = newCursor,
            Steps = actualSteps,
            Message = $"Undid {actualSteps} step(s). Now at position {newCursor}.",
            CurrentBytes = bytes
        };
    }

    public UndoRedoResult Redo(string id, int steps = 1)
    {
        var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
        var cursor = LoadCursorPosition(id, walCount);

        if (cursor >= walCount)
            return new UndoRedoResult { Position = cursor, Steps = 0, Message = "Already at the latest state. Nothing to redo." };

        var actualSteps = Math.Min(steps, walCount - cursor);
        var newCursor = cursor + actualSteps;

        var bytes = RebuildAndCheckpoint(id, newCursor);

        return new UndoRedoResult
        {
            Position = newCursor,
            Steps = actualSteps,
            Message = $"Redid {actualSteps} step(s). Now at position {newCursor}.",
            CurrentBytes = bytes
        };
    }

    public UndoRedoResult JumpTo(string id, int position)
    {
        var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();

        if (position < 0)
            position = 0;
        if (position > walCount)
        {
            var currentCursor = LoadCursorPosition(id, walCount);
            return new UndoRedoResult
            {
                Position = currentCursor,
                Steps = 0,
                Message = $"Position {position} is beyond the WAL (max {walCount}). No change."
            };
        }

        var oldCursor = LoadCursorPosition(id, walCount);
        if (position == oldCursor)
            return new UndoRedoResult { Position = position, Steps = 0, Message = $"Already at position {position}." };

        var bytes = RebuildAndCheckpoint(id, position);

        var stepsFromOld = Math.Abs(position - oldCursor);
        return new UndoRedoResult
        {
            Position = position,
            Steps = stepsFromOld,
            Message = $"Jumped to position {position}.",
            CurrentBytes = bytes
        };
    }

    public string? GetLastExternalSyncHash(string id)
    {
        try
        {
            var walEntries = ReadWalEntriesAsync(id).GetAwaiter().GetResult();
            var lastSync = walEntries
                .Where(e => e.EntryType is WalEntryType.ExternalSync or WalEntryType.Import && e.SyncMeta?.NewHash is not null)
                .LastOrDefault();
            return lastSync?.SyncMeta?.NewHash;
        }
        catch
        {
            return null;
        }
    }

    public HistoryResult GetHistory(string id, int offset = 0, int limit = 20)
    {
        var walEntries = ReadWalEntriesAsync(id).GetAwaiter().GetResult();
        var walCount = walEntries.Count;
        var cursor = LoadCursorPosition(id, walCount);

        var checkpointPositions = GetCheckpointPositionsAsync(id).GetAwaiter().GetResult();

        var entries = new List<HistoryEntry>();
        var startIdx = Math.Max(0, offset);
        var endIdx = Math.Min(walCount + 1, offset + limit);

        for (int i = startIdx; i < endIdx; i++)
        {
            if (i == 0)
            {
                entries.Add(new HistoryEntry
                {
                    Position = 0,
                    Timestamp = default,
                    Description = "Baseline (original document)",
                    IsCurrent = cursor == 0,
                    IsCheckpoint = true
                });
            }
            else
            {
                var walIdx = i - 1;
                if (walIdx < walEntries.Count)
                {
                    var we = walEntries[walIdx];
                    var historyEntry = new HistoryEntry
                    {
                        Position = i,
                        Timestamp = we.Timestamp,
                        Description = we.Description ?? "",
                        IsCurrent = cursor == i,
                        IsCheckpoint = checkpointPositions.Contains(i),
                        IsExternalSync = we.EntryType is WalEntryType.ExternalSync or WalEntryType.Import
                    };

                    if (we.EntryType is WalEntryType.ExternalSync or WalEntryType.Import && we.SyncMeta is not null)
                    {
                        historyEntry.SyncSummary = new ExternalSyncSummary
                        {
                            SourcePath = we.SyncMeta.SourcePath,
                            Added = we.SyncMeta.Summary.Added,
                            Removed = we.SyncMeta.Summary.Removed,
                            Modified = we.SyncMeta.Summary.Modified,
                            UncoveredCount = we.SyncMeta.UncoveredChanges.Count,
                            UncoveredTypes = we.SyncMeta.UncoveredChanges
                                .Select(u => u.Type.ToString().ToLowerInvariant())
                                .Distinct()
                                .ToList()
                        };
                    }

                    entries.Add(historyEntry);
                }
            }
        }

        return new HistoryResult
        {
            TotalEntries = walCount + 1,
            CursorPosition = cursor,
            CanUndo = cursor > 0,
            CanRedo = cursor < walCount,
            Entries = entries
        };
    }

    /// <summary>
    /// No-op for backward compatibility. Sessions are now stateless (loaded on demand from gRPC).
    /// </summary>
    [Obsolete("Sessions are now stateless. This method is a no-op.")]
    public int RestoreSessions() => 0;

    // --- gRPC Storage Helpers ---

    private async Task<int> GetWalEntryCountAsync(string sessionId)
    {
        var (entries, _) = await _history.ReadWalAsync(TenantId, sessionId);
        return entries.Count;
    }

    /// <summary>
    /// Load the cursor position from the index, or default to WAL count.
    /// </summary>
    private int LoadCursorPosition(string sessionId, int walCount)
    {
        try
        {
            var (indexData, found) = _history.LoadIndexAsync(TenantId).GetAwaiter().GetResult();
            if (found && indexData is not null)
            {
                var json = System.Text.Encoding.UTF8.GetString(indexData);
                var index = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionIndex);
                if (index is not null && index.TryGetValue(sessionId, out var entry))
                {
                    // Return cursor if valid, otherwise default to walCount
                    if (entry!.CursorPosition >= 0 && entry.CursorPosition <= walCount)
                    {
                        return entry.CursorPosition;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load cursor position for session {SessionId}, using default.", sessionId);
        }
        return walCount;
    }

    /// <summary>
    /// Load source_path from the gRPC index for a session.
    /// </summary>
    private string? LoadSourcePath(string sessionId)
    {
        try
        {
            var (indexData, found) = _history.LoadIndexAsync(TenantId).GetAwaiter().GetResult();
            if (found && indexData is not null)
            {
                var json = System.Text.Encoding.UTF8.GetString(indexData);
                var index = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionIndex);
                if (index is not null && index.TryGetValue(sessionId, out var entry))
                    return entry!.SourcePath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load source path for session {SessionId}.", sessionId);
        }
        return null;
    }

    private async Task<List<WalEntry>> ReadWalEntriesAsync(string sessionId)
    {
        var (grpcEntries, _) = await _history.ReadWalAsync(TenantId, sessionId);
        var entries = new List<WalEntry>();

        foreach (var grpcEntry in grpcEntries)
        {
            try
            {
                // The PatchJson field contains the serialized .NET WalEntry
                if (grpcEntry.PatchJson.Length > 0)
                {
                    var json = System.Text.Encoding.UTF8.GetString(grpcEntry.PatchJson);
                    var entry = JsonSerializer.Deserialize(json, WalJsonContext.Default.WalEntry);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize WAL entry for session {SessionId}.", sessionId);
            }
        }

        return entries;
    }

    private async Task AppendWalEntryAsync(string sessionId, WalEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, WalJsonContext.Default.WalEntry);
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);

        // GrpcWalEntry (WalEntryDto) is a positional record
        var grpcEntry = new GrpcWalEntry(
            Position: 0, // Server assigns position
            Operation: entry.EntryType.ToString(),
            Path: "",
            PatchJson: jsonBytes,
            Timestamp: entry.Timestamp
        );

        await _history.AppendWalAsync(TenantId, sessionId, new[] { grpcEntry });
    }

    private async Task TruncateWalAtAsync(string sessionId, int keepCount)
    {
        await _history.TruncateWalAsync(TenantId, sessionId, (ulong)keepCount);
    }

    // --- Private helpers ---

    private async Task PersistNewSessionAsync(DocxSession session)
    {
        var bytes = session.ToBytes();
        await _history.SaveSessionAsync(TenantId, session.Id, bytes);

        var now = DateTime.UtcNow;
        await _history.AddSessionToIndexAsync(TenantId, session.Id,
            new Grpc.SessionIndexEntryDto(
                session.SourcePath,
                now,
                now,
                0,
                Array.Empty<ulong>()));
    }

    /// <summary>
    /// Rebuild document at a given position, save checkpoint there, update cursor.
    /// Returns the serialized bytes at that position.
    /// </summary>
    private byte[] RebuildAndCheckpoint(string id, int targetPosition)
    {
        using var session = RebuildDocumentAtPositionAsync(id, targetPosition).GetAwaiter().GetResult();
        var bytes = session.ToBytes();

        // Save checkpoint at new position so future Get() is fast
        _history.SaveCheckpointAsync(TenantId, id, (ulong)targetPosition, bytes)
            .GetAwaiter().GetResult();

        // Update cursor + checkpoint in index
        _history.UpdateSessionInIndexAsync(TenantId, id,
            cursorPosition: (ulong)targetPosition,
            addCheckpointPositions: new[] { (ulong)targetPosition }).GetAwaiter().GetResult();

        return bytes;
    }

    private async Task<DocxSession> RebuildDocumentAtPositionAsync(string id, int targetPosition)
    {
        // Try to load checkpoint
        var (ckptData, ckptPos, ckptFound) = await _history.LoadCheckpointAsync(
            TenantId, id, (ulong)targetPosition);

        byte[] baseBytes;
        int checkpointPosition;

        if (ckptFound && ckptData is not null && (int)ckptPos <= targetPosition)
        {
            baseBytes = ckptData;
            checkpointPosition = (int)ckptPos;
        }
        else
        {
            // Fallback to baseline
            var (baselineData, _) = await _history.LoadSessionAsync(TenantId, id);
            baseBytes = baselineData ?? throw new InvalidOperationException($"No baseline found for session {id}");
            checkpointPosition = 0;
        }

        var sourcePath = LoadSourcePath(id);
        var session = DocxSession.FromBytes(baseBytes, id, sourcePath);

        // Replay patches from checkpoint to target
        if (targetPosition > checkpointPosition)
        {
            var walEntries = await ReadWalEntriesAsync(id);
            var patchesToReplay = walEntries
                .Skip(checkpointPosition)
                .Take(targetPosition - checkpointPosition)
                .Where(e => e.Patches is not null)
                .Select(e => e.Patches!)
                .ToList();

            foreach (var patchJson in patchesToReplay)
            {
                try
                {
                    ReplayPatch(session, patchJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to replay WAL entry during rebuild for session {SessionId}.", id);
                    break;
                }
            }
        }

        return session;
    }

    private static string GenerateDescription(string patchesJson)
    {
        try
        {
            var doc = JsonDocument.Parse(patchesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "patch";

            var ops = new List<string>();
            foreach (var patch in doc.RootElement.EnumerateArray())
            {
                var op = patch.TryGetProperty("op", out var opEl) ? opEl.GetString() : null;
                var path = patch.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                if (op is not null)
                {
                    if (op == "add_comment")
                    {
                        var cid = patch.TryGetProperty("comment_id", out var cidEl) ? cidEl.GetInt32().ToString() : "?";
                        ops.Add($"add_comment #{cid}");
                    }
                    else if (op == "delete_comment")
                    {
                        var cid = patch.TryGetProperty("comment_id", out var cidEl) ? cidEl.GetInt32().ToString() : "?";
                        ops.Add($"delete_comment #{cid}");
                    }
                    else if (op is "style_element" or "style_paragraph" or "style_table")
                    {
                        var stylePath = patch.TryGetProperty("path", out var spEl) && spEl.ValueKind == JsonValueKind.String
                            ? spEl.GetString()
                            : null;
                        ops.Add(stylePath is not null ? $"{op} {stylePath}" : $"{op} (all)");
                    }
                    else
                    {
                        var shortPath = path is not null && path.Length > 30
                            ? path[..30] + "..."
                            : path;
                        ops.Add(shortPath is not null ? $"{op} {shortPath}" : op);
                    }
                }
            }

            return ops.Count > 0 ? string.Join(", ", ops) : "patch";
        }
        catch
        {
            return "patch";
        }
    }

    private static void ReplayPatch(DocxSession session, string patchesJson)
    {
        var patchArray = JsonDocument.Parse(patchesJson).RootElement;
        if (patchArray.ValueKind != JsonValueKind.Array)
            return;

        var wpDoc = session.Document;
        var mainPart = wpDoc.MainDocumentPart
            ?? throw new InvalidOperationException("Document has no MainDocumentPart.");

        foreach (var patch in patchArray.EnumerateArray())
        {
            var op = patch.GetProperty("op").GetString()?.ToLowerInvariant();
            switch (op)
            {
                case "add":
                    Tools.PatchTool.ReplayAdd(patch, wpDoc, mainPart);
                    break;
                case "replace":
                    Tools.PatchTool.ReplayReplace(patch, wpDoc, mainPart);
                    break;
                case "remove":
                    Tools.PatchTool.ReplayRemove(patch, wpDoc);
                    break;
                case "move":
                    Tools.PatchTool.ReplayMove(patch, wpDoc);
                    break;
                case "copy":
                    Tools.PatchTool.ReplayCopy(patch, wpDoc);
                    break;
                case "replace_text":
                    Tools.PatchTool.ReplayReplaceText(patch, wpDoc);
                    break;
                case "remove_column":
                    Tools.PatchTool.ReplayRemoveColumn(patch, wpDoc);
                    break;
                case "add_comment":
                    Tools.CommentTools.ReplayAddComment(patch, wpDoc);
                    break;
                case "delete_comment":
                    Tools.CommentTools.ReplayDeleteComment(patch, wpDoc);
                    break;
                case "style_element":
                    Tools.StyleTools.ReplayStyleElement(patch, wpDoc);
                    break;
                case "style_paragraph":
                    Tools.StyleTools.ReplayStyleParagraph(patch, wpDoc);
                    break;
                case "style_table":
                    Tools.StyleTools.ReplayStyleTable(patch, wpDoc);
                    break;
                case "accept_revision":
                    Tools.RevisionTools.ReplayAcceptRevision(patch, wpDoc);
                    break;
                case "reject_revision":
                    Tools.RevisionTools.ReplayRejectRevision(patch, wpDoc);
                    break;
                case "track_changes_enable":
                    Tools.RevisionTools.ReplayTrackChangesEnable(patch, wpDoc);
                    break;
            }
        }
    }
}
