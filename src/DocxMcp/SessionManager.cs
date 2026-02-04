using System.Collections.Concurrent;
using System.Text.Json;
using DocxMcp.Persistence;
using Microsoft.Extensions.Logging;

namespace DocxMcp;

/// <summary>
/// Thread-safe manager for document sessions with WAL-based persistence.
/// Sessions survive server restarts via baseline snapshots + write-ahead log replay.
/// Supports undo/redo via WAL cursor + checkpoint replay.
/// Uses cross-process file locking to prevent index corruption when multiple
/// MCP server processes share the same sessions directory.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, DocxSession> _sessions = new();
    private readonly ConcurrentDictionary<string, int> _cursors = new();
    private readonly SessionStore _store;
    private readonly ILogger<SessionManager> _logger;
    private SessionIndexFile _index;
    private readonly object _indexLock = new();
    private readonly int _compactThreshold;
    private readonly int _checkpointInterval;

    public SessionManager(SessionStore store, ILogger<SessionManager> logger)
    {
        _store = store;
        _logger = logger;
        _index = new SessionIndexFile();

        var thresholdEnv = Environment.GetEnvironmentVariable("DOCX_WAL_COMPACT_THRESHOLD");
        _compactThreshold = int.TryParse(thresholdEnv, out var t) && t > 0 ? t : 50;

        var intervalEnv = Environment.GetEnvironmentVariable("DOCX_CHECKPOINT_INTERVAL");
        _checkpointInterval = int.TryParse(intervalEnv, out var ci) && ci > 0 ? ci : 10;
    }

    public DocxSession Open(string path)
    {
        var session = DocxSession.Open(path);
        if (!_sessions.TryAdd(session.Id, session))
        {
            session.Dispose();
            throw new InvalidOperationException("Session ID collision — this should not happen.");
        }

        PersistNewSession(session);
        return session;
    }

    public DocxSession Create()
    {
        var session = DocxSession.Create();
        if (!_sessions.TryAdd(session.Id, session))
        {
            session.Dispose();
            throw new InvalidOperationException("Session ID collision — this should not happen.");
        }

        PersistNewSession(session);
        return session;
    }

    public DocxSession Get(string id)
    {
        if (_sessions.TryGetValue(id, out var session))
            return session;
        throw new KeyNotFoundException($"No document session with ID '{id}'.");
    }

    /// <summary>
    /// Resolve a session by ID or file path.
    /// - If the input looks like a session ID and matches, returns that session.
    /// - If the input is a file path, checks for existing session with that path.
    /// - If no existing session found and file exists, auto-opens a new session.
    /// </summary>
    /// <param name="idOrPath">Either a session ID (12 hex chars) or a file path.</param>
    /// <returns>The resolved session.</returns>
    /// <exception cref="KeyNotFoundException">If no session found and file doesn't exist.</exception>
    public DocxSession ResolveSession(string idOrPath)
    {
        // First, try as session ID
        if (_sessions.TryGetValue(idOrPath, out var session))
            return session;

        // Check if it looks like a file path (has extension, path separator, or starts with ~ or /)
        var isLikelyPath = idOrPath.Contains(Path.DirectorySeparatorChar)
            || idOrPath.Contains(Path.AltDirectorySeparatorChar)
            || idOrPath.StartsWith('~')
            || idOrPath.StartsWith('.')
            || Path.HasExtension(idOrPath);

        if (!isLikelyPath)
        {
            // Doesn't look like a path, treat as missing session ID
            throw new KeyNotFoundException($"No document session with ID '{idOrPath}'.");
        }

        // Expand ~ to home directory
        var expandedPath = idOrPath;
        if (expandedPath.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expandedPath = Path.Combine(home, expandedPath[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        // Resolve to absolute path
        var absolutePath = Path.GetFullPath(expandedPath);

        // Check if we have an existing session for this path
        var existing = _sessions.Values.FirstOrDefault(s =>
            s.SourcePath is not null &&
            string.Equals(s.SourcePath, absolutePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
            return existing;

        // Auto-open if file exists
        if (File.Exists(absolutePath))
            return Open(absolutePath);

        throw new KeyNotFoundException($"No session found for '{idOrPath}' and file does not exist.");
    }

    public void Save(string id, string? path = null)
    {
        var session = Get(id);
        session.Save(path);
        // Note: WAL is intentionally preserved after save.
        // Compaction should only be triggered explicitly via CLI.
    }

    public void Close(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            _cursors.TryRemove(id, out _);
            session.Dispose();
            _store.DeleteSession(id);

            WithLockedIndex(index => { index.Sessions.RemoveAll(e => e.Id == id); });
        }
        else
        {
            throw new KeyNotFoundException($"No document session with ID '{id}'.");
        }
    }

    public IReadOnlyList<(string Id, string? Path)> List()
    {
        return _sessions.Values
            .Select(s => (s.Id, s.SourcePath))
            .ToList()
            .AsReadOnly();
    }

    // --- WAL operations ---

    /// <summary>
    /// Append a patch to the WAL after a successful mutation.
    /// If the cursor is behind the WAL tip (after undo), truncates future entries first.
    /// Creates checkpoints at interval boundaries.
    /// Triggers automatic compaction when WAL exceeds threshold (default 50 entries).
    /// </summary>
    public void AppendWal(string id, string patchesJson, string? description = null)
    {
        try
        {
            var cursor = _cursors.GetOrAdd(id, 0);
            var walCount = _store.WalEntryCount(id);

            // If cursor < walCount, we're in an undo state — truncate future
            if (cursor < walCount)
            {
                _store.TruncateWalAt(id, cursor);

                WithLockedIndex(index =>
                {
                    var entry = index.Sessions.Find(e => e.Id == id);
                    if (entry is not null)
                    {
                        _store.DeleteCheckpointsAfter(id, cursor, entry.CheckpointPositions);
                        entry.CheckpointPositions.RemoveAll(p => p > cursor);
                    }
                });
            }

            // Auto-generate description from patch ops if not provided
            description ??= GenerateDescription(patchesJson);

            _store.AppendWal(id, patchesJson, description);
            var newCursor = cursor + 1;
            _cursors[id] = newCursor;

            // Create checkpoint if crossing an interval boundary
            MaybeCreateCheckpoint(id, newCursor);

            // Update index and extract compaction decision BEFORE releasing lock
            // to avoid recursive deadlock (AppendWal -> Compact -> WithLockedIndex)
            bool shouldCompact = false;
            WithLockedIndex(index =>
            {
                var entry = index.Sessions.Find(e => e.Id == id);
                if (entry is not null)
                {
                    entry.WalCount = _store.WalEntryCount(id);
                    entry.CursorPosition = newCursor;
                    entry.LastModifiedAt = DateTime.UtcNow;
                    shouldCompact = entry.WalCount >= _compactThreshold;
                }
            });

            // Compact AFTER releasing the file lock to avoid deadlock
            if (shouldCompact)
                Compact(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append WAL for session {SessionId}.", id);
        }
    }

    /// <summary>
    /// Create a new baseline snapshot from the current in-memory state and truncate the WAL.
    /// Refuses if redo entries exist unless discardRedoHistory is true.
    /// </summary>
    public void Compact(string id, bool discardRedoHistory = false)
    {
        try
        {
            var cursor = _cursors.GetOrAdd(id, _ => _store.WalEntryCount(id));
            var walCount = _store.WalEntryCount(id);

            if (cursor < walCount && !discardRedoHistory)
            {
                _logger.LogInformation(
                    "Skipping compaction for session {SessionId}: {RedoCount} redo entries exist. Use discardRedoHistory=true to force.",
                    id, walCount - cursor);
                return;
            }

            var session = Get(id);
            var bytes = session.ToBytes();
            _store.PersistBaseline(id, bytes);
            _store.TruncateWal(id);
            _store.DeleteCheckpoints(id);
            _cursors[id] = 0;

            WithLockedIndex(index =>
            {
                var entry = index.Sessions.Find(e => e.Id == id);
                if (entry is not null)
                {
                    entry.WalCount = 0;
                    entry.CursorPosition = 0;
                    entry.CheckpointPositions.Clear();
                    entry.LastModifiedAt = DateTime.UtcNow;
                }
            });

            _logger.LogInformation("Compacted session {SessionId}.", id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compact session {SessionId}.", id);
        }
    }

    /// <summary>
    /// Append an external sync entry to the WAL.
    /// Truncates future entries if in undo state, creates checkpoint from the sync's DocumentSnapshot,
    /// and replaces the in-memory session.
    /// </summary>
    /// <param name="id">Session ID.</param>
    /// <param name="syncEntry">The WAL entry with ExternalSync type and SyncMeta.</param>
    /// <param name="newSession">The new session to replace the current one.</param>
    /// <returns>The new WAL position after append.</returns>
    public int AppendExternalSync(string id, WalEntry syncEntry, DocxSession newSession)
    {
        try
        {
            var cursor = _cursors.GetOrAdd(id, 0);
            var walCount = _store.WalEntryCount(id);

            // If cursor < walCount, we're in an undo state — truncate future
            if (cursor < walCount)
            {
                _store.TruncateWalAt(id, cursor);

                WithLockedIndex(index =>
                {
                    var entry = index.Sessions.Find(e => e.Id == id);
                    if (entry is not null)
                    {
                        _store.DeleteCheckpointsAfter(id, cursor, entry.CheckpointPositions);
                        entry.CheckpointPositions.RemoveAll(p => p > cursor);
                    }
                });
            }

            // Serialize and append WAL entry
            var walLine = System.Text.Json.JsonSerializer.Serialize(syncEntry, WalJsonContext.Default.WalEntry);
            _store.GetOrCreateWal(id).Append(walLine);

            var newCursor = cursor + 1;
            _cursors[id] = newCursor;

            // Create checkpoint using the stored DocumentSnapshot (sync always forces a checkpoint)
            // Use import checkpoint path for Import entries, regular checkpoint path for ExternalSync
            if (syncEntry.SyncMeta?.DocumentSnapshot is not null)
            {
                if (syncEntry.EntryType == WalEntryType.Import)
                    _store.PersistImportCheckpoint(id, newCursor, syncEntry.SyncMeta.DocumentSnapshot);
                else
                    _store.PersistCheckpoint(id, newCursor, syncEntry.SyncMeta.DocumentSnapshot);
            }

            // Replace in-memory session
            var oldSession = _sessions[id];
            _sessions[id] = newSession;
            oldSession.Dispose();

            // Update index
            WithLockedIndex(index =>
            {
                var entry = index.Sessions.Find(e => e.Id == id);
                if (entry is not null)
                {
                    entry.WalCount = _store.WalEntryCount(id);
                    entry.CursorPosition = newCursor;
                    entry.LastModifiedAt = DateTime.UtcNow;
                    if (!entry.CheckpointPositions.Contains(newCursor))
                    {
                        entry.CheckpointPositions.Add(newCursor);
                    }
                }
            });

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

    /// <summary>
    /// Undo N steps by decrementing the cursor and rebuilding from the nearest checkpoint.
    /// </summary>
    public UndoRedoResult Undo(string id, int steps = 1)
    {
        var session = Get(id); // validate session exists
        var cursor = _cursors.GetOrAdd(id, _ => _store.WalEntryCount(id));

        if (cursor <= 0)
            return new UndoRedoResult { Position = 0, Steps = 0, Message = "Already at the beginning. Nothing to undo." };

        var actualSteps = Math.Min(steps, cursor);
        var newCursor = cursor - actualSteps;

        RebuildDocumentAtPosition(id, newCursor);

        return new UndoRedoResult
        {
            Position = newCursor,
            Steps = actualSteps,
            Message = $"Undid {actualSteps} step(s). Now at position {newCursor}."
        };
    }

    /// <summary>
    /// Redo N steps by incrementing the cursor and replaying patches on the current DOM.
    /// For ExternalSync entries, uses checkpoint-based rebuild instead of patch replay.
    /// </summary>
    public UndoRedoResult Redo(string id, int steps = 1)
    {
        var session = Get(id); // validate session exists
        var cursor = _cursors.GetOrAdd(id, _ => _store.WalEntryCount(id));
        var walCount = _store.WalEntryCount(id);

        if (cursor >= walCount)
            return new UndoRedoResult { Position = cursor, Steps = 0, Message = "Already at the latest state. Nothing to redo." };

        var actualSteps = Math.Min(steps, walCount - cursor);
        var newCursor = cursor + actualSteps;

        // Check if any entries in the redo range are ExternalSync or Import
        var walEntries = _store.ReadWalEntries(id);
        var hasExternalSync = false;
        for (int i = cursor; i < newCursor && i < walEntries.Count; i++)
        {
            if (walEntries[i].EntryType is WalEntryType.ExternalSync or WalEntryType.Import)
            {
                hasExternalSync = true;
                break;
            }
        }

        if (hasExternalSync)
        {
            // ExternalSync entries have checkpoints, so rebuild from checkpoint
            RebuildDocumentAtPosition(id, newCursor);
        }
        else
        {
            // Regular patches: replay on current DOM (fast, no rebuild)
            var patches = _store.ReadWalRange(id, cursor, newCursor);
            foreach (var patchJson in patches)
            {
                ReplayPatch(session, patchJson);
            }

            _cursors[id] = newCursor;

            WithLockedIndex(index =>
            {
                var entry = index.Sessions.Find(e => e.Id == id);
                if (entry is not null)
                {
                    entry.CursorPosition = newCursor;
                }
            });
        }

        return new UndoRedoResult
        {
            Position = newCursor,
            Steps = actualSteps,
            Message = $"Redid {actualSteps} step(s). Now at position {newCursor}."
        };
    }

    /// <summary>
    /// Jump to an arbitrary WAL position by rebuilding from the nearest checkpoint.
    /// </summary>
    public UndoRedoResult JumpTo(string id, int position)
    {
        var session = Get(id); // validate session exists
        var walCount = _store.WalEntryCount(id);

        if (position < 0)
            position = 0;
        if (position > walCount)
            return new UndoRedoResult
            {
                Position = _cursors.GetOrAdd(id, _ => walCount),
                Steps = 0,
                Message = $"Position {position} is beyond the WAL (max {walCount}). No change."
            };

        var oldCursor = _cursors.GetOrAdd(id, _ => walCount);
        if (position == oldCursor)
            return new UndoRedoResult { Position = position, Steps = 0, Message = $"Already at position {position}." };

        RebuildDocumentAtPosition(id, position);

        var stepsFromOld = Math.Abs(position - oldCursor);
        return new UndoRedoResult
        {
            Position = position,
            Steps = stepsFromOld,
            Message = $"Jumped to position {position}."
        };
    }

    /// <summary>
    /// Get the hash of the external file from the last ExternalSync WAL entry.
    /// Used to detect if the external file has changed since the last sync.
    /// </summary>
    public string? GetLastExternalSyncHash(string id)
    {
        try
        {
            var walEntries = _store.ReadWalEntries(id);
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

    /// <summary>
    /// Get the edit history for a session with metadata.
    /// </summary>
    public HistoryResult GetHistory(string id, int offset = 0, int limit = 20)
    {
        Get(id); // validate session exists
        var walEntries = _store.ReadWalEntries(id);
        var cursor = _cursors.GetOrAdd(id, _ => walEntries.Count);
        var walCount = walEntries.Count;

        var checkpointPositions = WithLockedIndex(index =>
        {
            var entry = index.Sessions.Find(e => e.Id == id);
            return entry?.CheckpointPositions.ToList() ?? new List<int>();
        });

        var entries = new List<HistoryEntry>();

        // Include position 0 (baseline) as the first entry
        var startIdx = Math.Max(0, offset);
        var endIdx = Math.Min(walCount + 1, offset + limit); // +1 for baseline

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

                    // Populate sync summary for external sync / import entries
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
            TotalEntries = walCount + 1, // +1 for baseline
            CursorPosition = cursor,
            CanUndo = cursor > 0,
            CanRedo = cursor < walCount,
            Entries = entries
        };
    }

    /// <summary>
    /// Restore all persisted sessions from disk on startup.
    /// Acquires file lock for the entire duration to prevent mutations during startup replay.
    /// Loads from the nearest checkpoint when available to properly restore ExternalSync state.
    /// Note: Sessions are never auto-deleted. Use CLI to manually close/clean sessions.
    /// </summary>
    public int RestoreSessions()
    {
        _store.EnsureDirectory();
        using var fileLock = _store.AcquireLock();

        lock (_indexLock)
        {
            _index = _store.LoadIndex();
        }

        int restored = 0;

        foreach (var entry in _index.Sessions.ToList())
        {
            try
            {
                // Determine how many WAL entries to replay (up to cursor position)
                var walCount = _store.WalEntryCount(entry.Id);
                var cursorTarget = entry.CursorPosition;

                // Backward compat: old entries without cursor tracking (sentinel -1)
                if (cursorTarget < 0)
                    cursorTarget = walCount;

                var replayCount = Math.Min(cursorTarget, walCount);

                // Load from nearest checkpoint instead of baseline + full replay.
                // This is critical for ExternalSync entries which store document snapshots
                // in checkpoints rather than as replayable patches.
                var (ckptPos, ckptBytes) = _store.LoadNearestCheckpoint(
                    entry.Id,
                    replayCount,
                    entry.CheckpointPositions);

                var session = DocxSession.FromBytes(ckptBytes, entry.Id, entry.SourcePath);

                // Only replay patches AFTER the checkpoint position
                if (replayCount > ckptPos)
                {
                    var patches = _store.ReadWalRange(entry.Id, ckptPos, replayCount);
                    foreach (var patchJson in patches)
                    {
                        try
                        {
                            ReplayPatch(session, patchJson);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to replay WAL entry for session {SessionId}; stopping replay.",
                                entry.Id);
                            break;
                        }
                    }
                }

                if (_sessions.TryAdd(session.Id, session))
                {
                    _cursors[session.Id] = replayCount;
                    restored++;
                }
                else
                    session.Dispose();
            }
            catch (Exception ex)
            {
                // Log but don't delete — WAL history is preserved.
                // Use CLI 'close' command to manually remove corrupt sessions.
                _logger.LogWarning(ex, "Failed to restore session {SessionId}; skipping (WAL preserved).", entry.Id);
            }
        }

        return restored;
    }

    // --- Cross-process index helpers ---

    /// <summary>
    /// Acquire cross-process file lock, reload index from disk, mutate, save.
    /// Ensures no stale reads when multiple processes share the sessions directory.
    /// </summary>
    private void WithLockedIndex(Action<SessionIndexFile> mutate)
    {
        using var fileLock = _store.AcquireLock();
        lock (_indexLock)
        {
            _index = _store.LoadIndex();
            mutate(_index);
            _store.SaveIndex(_index);
        }
    }

    /// <summary>
    /// Acquire cross-process file lock, reload index from disk, read a value.
    /// </summary>
    private T WithLockedIndex<T>(Func<SessionIndexFile, T> read)
    {
        using var fileLock = _store.AcquireLock();
        lock (_indexLock)
        {
            _index = _store.LoadIndex();
            return read(_index);
        }
    }

    // --- Private helpers ---

    private void PersistNewSession(DocxSession session)
    {
        try
        {
            var bytes = session.ToBytes();
            _store.PersistBaseline(session.Id, bytes);
            _store.GetOrCreateWal(session.Id); // create empty WAL mapping

            _cursors[session.Id] = 0;

            WithLockedIndex(index =>
            {
                index.Sessions.Add(new SessionEntry
                {
                    Id = session.Id,
                    SourcePath = session.SourcePath,
                    CreatedAt = DateTime.UtcNow,
                    LastModifiedAt = DateTime.UtcNow,
                    DocxFile = $"{session.Id}.docx",
                    WalCount = 0,
                    CursorPosition = 0
                });
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist new session {SessionId}.", session.Id);
        }
    }

    /// <summary>
    /// Rebuild the in-memory document at a specific WAL position.
    /// Loads the nearest checkpoint, replays patches to the target position,
    /// and replaces the in-memory session.
    /// </summary>
    private void RebuildDocumentAtPosition(string id, int targetPosition)
    {
        var checkpointPositions = WithLockedIndex(index =>
        {
            var indexEntry = index.Sessions.Find(e => e.Id == id);
            return indexEntry?.CheckpointPositions.ToList() ?? new List<int>();
        });

        var (ckptPos, ckptBytes) = _store.LoadNearestCheckpoint(id, targetPosition, checkpointPositions);

        var oldSession = Get(id);
        var newSession = DocxSession.FromBytes(ckptBytes, oldSession.Id, oldSession.SourcePath);

        // Replay patches from checkpoint position to target
        if (targetPosition > ckptPos)
        {
            var patches = _store.ReadWalRange(id, ckptPos, targetPosition);
            foreach (var patchJson in patches)
            {
                try
                {
                    ReplayPatch(newSession, patchJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to replay WAL entry during rebuild for session {SessionId}.", id);
                    break;
                }
            }
        }

        // Replace in-memory session
        _sessions[id] = newSession;
        _cursors[id] = targetPosition;
        oldSession.Dispose();

        WithLockedIndex(index =>
        {
            var entry = index.Sessions.Find(e => e.Id == id);
            if (entry is not null)
            {
                entry.CursorPosition = targetPosition;
            }
        });
    }

    /// <summary>
    /// Create a checkpoint if the new cursor crosses a checkpoint interval boundary.
    /// </summary>
    private void MaybeCreateCheckpoint(string id, int newCursor)
    {
        if (newCursor > 0 && newCursor % _checkpointInterval == 0)
        {
            try
            {
                var session = Get(id);
                var bytes = session.ToBytes();
                _store.PersistCheckpoint(id, newCursor, bytes);

                WithLockedIndex(index =>
                {
                    var entry = index.Sessions.Find(e => e.Id == id);
                    if (entry is not null && !entry.CheckpointPositions.Contains(newCursor))
                    {
                        entry.CheckpointPositions.Add(newCursor);
                    }
                });

                _logger.LogInformation("Created checkpoint at position {Position} for session {SessionId}.", newCursor, id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create checkpoint at position {Position} for session {SessionId}.", newCursor, id);
            }
        }
    }

    /// <summary>
    /// Generate a description string from patch operations.
    /// </summary>
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

    /// <summary>
    /// Replay a single patch operation against a session's document.
    /// Uses the same logic as PatchTool.ApplyPatch but without MCP tool wiring.
    /// </summary>
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
