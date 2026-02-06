using System.Collections.Concurrent;
using System.Text.Json;
using DocxMcp.ExternalChanges;
using DocxMcp.Grpc;
using DocxMcp.Persistence;
using Microsoft.Extensions.Logging;

using GrpcWalEntry = DocxMcp.Grpc.WalEntryDto;
using WalEntry = DocxMcp.Persistence.WalEntry;

namespace DocxMcp;

/// <summary>
/// Thread-safe manager for document sessions with gRPC-based persistence.
/// Sessions are stored via a gRPC storage service with multi-tenant isolation.
/// Supports undo/redo via WAL cursor + checkpoint replay.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, DocxSession> _sessions = new();
    private readonly ConcurrentDictionary<string, int> _cursors = new();
    private readonly IStorageClient _storage;
    private readonly ILogger<SessionManager> _logger;
    private readonly string _tenantId;
    private readonly int _compactThreshold;
    private readonly int _checkpointInterval;
    private readonly bool _autoSaveEnabled;
    private ExternalChangeTracker? _externalChangeTracker;

    /// <summary>
    /// The tenant ID for this SessionManager instance.
    /// Captured at construction time to ensure consistency across threads.
    /// </summary>
    public string TenantId => _tenantId;

    /// <summary>
    /// Create a SessionManager with the specified tenant ID.
    /// If tenantId is null, uses the current tenant from TenantContextHelper.
    /// </summary>
    public SessionManager(IStorageClient storage, ILogger<SessionManager> logger, string? tenantId = null)
    {
        _storage = storage;
        _logger = logger;
        _tenantId = tenantId ?? TenantContextHelper.CurrentTenantId;

        var thresholdEnv = Environment.GetEnvironmentVariable("DOCX_WAL_COMPACT_THRESHOLD");
        _compactThreshold = int.TryParse(thresholdEnv, out var t) && t > 0 ? t : 50;

        var intervalEnv = Environment.GetEnvironmentVariable("DOCX_CHECKPOINT_INTERVAL");
        _checkpointInterval = int.TryParse(intervalEnv, out var ci) && ci > 0 ? ci : 10;

        var autoSaveEnv = Environment.GetEnvironmentVariable("DOCX_AUTO_SAVE");
        _autoSaveEnabled = autoSaveEnv is null || !string.Equals(autoSaveEnv, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Set the external change tracker (setter injection to avoid circular dependency).
    /// </summary>
    public void SetExternalChangeTracker(ExternalChangeTracker tracker)
    {
        _externalChangeTracker = tracker;
    }

    public DocxSession Open(string path)
    {
        var session = DocxSession.Open(path);
        if (!_sessions.TryAdd(session.Id, session))
        {
            session.Dispose();
            throw new InvalidOperationException("Session ID collision — this should not happen.");
        }

        PersistNewSessionAsync(session).GetAwaiter().GetResult();
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

        PersistNewSessionAsync(session).GetAwaiter().GetResult();
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
    public DocxSession ResolveSession(string idOrPath)
    {
        // First, try as session ID
        if (_sessions.TryGetValue(idOrPath, out var session))
            return session;

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

        // If path is provided, update/register the source first
        if (path is not null)
        {
            SetSource(id, path, autoSync: _autoSaveEnabled);
        }

        // Ensure source is registered
        var status = _storage.GetSyncStatusAsync(TenantId, id).GetAwaiter().GetResult();
        if (status is null)
        {
            throw new InvalidOperationException(
                $"No save target registered for session '{id}'. Use document_set_source to set a path first.");
        }

        // Sync to source via gRPC
        var data = session.ToBytes();
        var (success, error, _) = _storage.SyncToSourceAsync(TenantId, id, data).GetAwaiter().GetResult();

        if (!success)
        {
            throw new InvalidOperationException($"Failed to save session '{id}': {error}");
        }

        _externalChangeTracker?.UpdateSessionSnapshot(id);
        _logger.LogDebug("Saved session {SessionId} to {Path}.", id, status.Uri);
    }

    public void Close(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            _cursors.TryRemove(id, out _);
            session.Dispose();

            _storage.DeleteSessionAsync(TenantId, id).GetAwaiter().GetResult();
            _storage.RemoveSessionFromIndexAsync(TenantId, id).GetAwaiter().GetResult();
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
    /// </summary>
    public void AppendWal(string id, string patchesJson, string? description = null)
    {
        try
        {
            var cursor = _cursors.GetOrAdd(id, 0);
            var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();

            // If cursor < walCount, we're in an undo state — truncate future
            if (cursor < walCount)
            {
                TruncateWalAtAsync(id, cursor).GetAwaiter().GetResult();

                // Remove checkpoints above cursor position
                var checkpointsToRemove = GetCheckpointPositionsAboveAsync(id, (ulong)cursor).GetAwaiter().GetResult();
                if (checkpointsToRemove.Count > 0)
                {
                    _storage.UpdateSessionInIndexAsync(TenantId, id,
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
            _cursors[id] = newCursor;

            // Create checkpoint if crossing an interval boundary
            MaybeCreateCheckpointAsync(id, newCursor).GetAwaiter().GetResult();

            // Update index with new WAL position
            var newWalCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _storage.UpdateSessionInIndexAsync(TenantId, id,
                modifiedAtUnix: now,
                walPosition: (ulong)newWalCount).GetAwaiter().GetResult();

            // Check if compaction is needed
            if ((ulong)newWalCount >= (ulong)_compactThreshold)
                Compact(id);

            MaybeAutoSave(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append WAL for session {SessionId}.", id);
        }
    }

    private async Task<List<ulong>> GetCheckpointPositionsAboveAsync(string id, ulong threshold)
    {
        var (indexData, found) = await _storage.LoadIndexAsync(TenantId);
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
        var (indexData, found) = await _storage.LoadIndexAsync(TenantId);
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
            var cursor = _cursors.GetOrAdd(id, _ => walCount);

            if (cursor < walCount && !discardRedoHistory)
            {
                _logger.LogInformation(
                    "Skipping compaction for session {SessionId}: {RedoCount} redo entries exist.",
                    id, walCount - cursor);
                return;
            }

            var session = Get(id);
            var bytes = session.ToBytes();

            _storage.SaveSessionAsync(TenantId, id, bytes).GetAwaiter().GetResult();
            _storage.TruncateWalAsync(TenantId, id, 0).GetAwaiter().GetResult();
            _cursors[id] = 0;

            // Get all checkpoint positions to remove
            var checkpointsToRemove = GetCheckpointPositionsAboveAsync(id, 0).GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _storage.UpdateSessionInIndexAsync(TenantId, id,
                modifiedAtUnix: now,
                walPosition: 0,
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
    public int AppendExternalSync(string id, WalEntry syncEntry, DocxSession newSession)
    {
        try
        {
            var cursor = _cursors.GetOrAdd(id, 0);
            var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();

            // If cursor < walCount, we're in an undo state — truncate future
            if (cursor < walCount)
            {
                TruncateWalAtAsync(id, cursor).GetAwaiter().GetResult();

                // Remove checkpoints above cursor position
                var checkpointsToRemove = GetCheckpointPositionsAboveAsync(id, (ulong)cursor).GetAwaiter().GetResult();
                if (checkpointsToRemove.Count > 0)
                {
                    _storage.UpdateSessionInIndexAsync(TenantId, id,
                        removeCheckpointPositions: checkpointsToRemove).GetAwaiter().GetResult();
                }
            }

            AppendWalEntryAsync(id, syncEntry).GetAwaiter().GetResult();

            var newCursor = cursor + 1;
            _cursors[id] = newCursor;

            // Create checkpoint using the stored DocumentSnapshot
            if (syncEntry.SyncMeta?.DocumentSnapshot is not null)
            {
                _storage.SaveCheckpointAsync(TenantId, id, (ulong)newCursor, syncEntry.SyncMeta.DocumentSnapshot)
                    .GetAwaiter().GetResult();
            }

            // Replace in-memory session
            var oldSession = _sessions[id];
            _sessions[id] = newSession;
            oldSession.Dispose();

            // Update index with new WAL position and checkpoint
            var newWalCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _storage.UpdateSessionInIndexAsync(TenantId, id,
                modifiedAtUnix: now,
                walPosition: (ulong)newWalCount,
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
        var session = Get(id);
        var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
        var cursor = _cursors.GetOrAdd(id, _ => walCount);

        if (cursor <= 0)
            return new UndoRedoResult { Position = 0, Steps = 0, Message = "Already at the beginning. Nothing to undo." };

        var actualSteps = Math.Min(steps, cursor);
        var newCursor = cursor - actualSteps;

        RebuildDocumentAtPositionAsync(id, newCursor).GetAwaiter().GetResult();
        MaybeAutoSave(id);

        return new UndoRedoResult
        {
            Position = newCursor,
            Steps = actualSteps,
            Message = $"Undid {actualSteps} step(s). Now at position {newCursor}."
        };
    }

    public UndoRedoResult Redo(string id, int steps = 1)
    {
        var session = Get(id);
        var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();
        var cursor = _cursors.GetOrAdd(id, _ => walCount);

        if (cursor >= walCount)
            return new UndoRedoResult { Position = cursor, Steps = 0, Message = "Already at the latest state. Nothing to redo." };

        var actualSteps = Math.Min(steps, walCount - cursor);
        var newCursor = cursor + actualSteps;

        // Check if any entries in the redo range are ExternalSync or Import
        var walEntries = ReadWalEntriesAsync(id).GetAwaiter().GetResult();
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
            RebuildDocumentAtPositionAsync(id, newCursor).GetAwaiter().GetResult();
        }
        else
        {
            var patches = walEntries.Skip(cursor).Take(newCursor - cursor)
                .Where(e => e.Patches is not null)
                .Select(e => e.Patches!)
                .ToList();

            foreach (var patchJson in patches)
            {
                ReplayPatch(session, patchJson);
            }

            _cursors[id] = newCursor;
        }

        MaybeAutoSave(id);

        return new UndoRedoResult
        {
            Position = newCursor,
            Steps = actualSteps,
            Message = $"Redid {actualSteps} step(s). Now at position {newCursor}."
        };
    }

    public UndoRedoResult JumpTo(string id, int position)
    {
        var session = Get(id);
        var walCount = GetWalEntryCountAsync(id).GetAwaiter().GetResult();

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

        RebuildDocumentAtPositionAsync(id, position).GetAwaiter().GetResult();
        MaybeAutoSave(id);

        var stepsFromOld = Math.Abs(position - oldCursor);
        return new UndoRedoResult
        {
            Position = position,
            Steps = stepsFromOld,
            Message = $"Jumped to position {position}."
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
        Get(id);
        var walEntries = ReadWalEntriesAsync(id).GetAwaiter().GetResult();
        var walCount = walEntries.Count;
        var cursor = _cursors.GetOrAdd(id, _ => walCount);

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
    /// Restore all persisted sessions from the gRPC storage service on startup.
    /// </summary>
    public int RestoreSessions()
    {
        return RestoreSessionsAsync().GetAwaiter().GetResult();
    }

    private async Task<int> RestoreSessionsAsync()
    {
        // Load the index to get list of sessions
        var (indexData, found) = await _storage.LoadIndexAsync(TenantId);
        if (!found || indexData is null)
        {
            _logger.LogInformation("No session index found for tenant {TenantId}; nothing to restore.", TenantId);
            return 0;
        }

        SessionIndex index;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(indexData);
            var parsed = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionIndex);
            if (parsed is null)
            {
                _logger.LogWarning("Failed to parse session index for tenant {TenantId}.", TenantId);
                return 0;
            }
            index = parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize session index for tenant {TenantId}.", TenantId);
            return 0;
        }

        int restored = 0;

        foreach (var entry in index.Sessions.ToList())
        {
            var sessionId = entry.Id;
            try
            {
                // Try to read WAL entries (may fail for legacy binary format)
                List<WalEntry> walEntries = [];
                int walCount = 0;
                bool walReadFailed = false;
                try
                {
                    walEntries = await ReadWalEntriesAsync(sessionId);
                    walCount = walEntries.Count;
                }
                catch (Exception walEx)
                {
                    // WAL may be in legacy binary format - log and continue without replay
                    _logger.LogDebug(walEx, "Could not read WAL for session {SessionId} (may be legacy format); skipping replay.", sessionId);
                    walReadFailed = true;
                }

                // Use WAL position as cursor target (cursor is now local only)
                var cursorTarget = (int)entry.WalPosition;
                if (cursorTarget < 0)
                    cursorTarget = walCount;
                var replayCount = Math.Min(cursorTarget, walCount);

                // Load from nearest checkpoint or baseline
                byte[] sessionBytes;
                int checkpointPosition = 0;

                // First try latest checkpoint
                var (ckptData, ckptPos, ckptFound) = await _storage.LoadCheckpointAsync(
                    TenantId, sessionId, (ulong)replayCount);

                if (ckptFound && ckptData is not null)
                {
                    sessionBytes = ckptData;
                    checkpointPosition = (int)ckptPos;
                }
                else
                {
                    // Fallback to baseline
                    var (baselineData, baselineFound) = await _storage.LoadSessionAsync(TenantId, sessionId);
                    if (!baselineFound || baselineData is null)
                    {
                        _logger.LogWarning("Session {SessionId} has no baseline; skipping.", sessionId);
                        continue;
                    }
                    sessionBytes = baselineData;
                    checkpointPosition = 0;
                }

                DocxSession session;
                try
                {
                    session = DocxSession.FromBytes(sessionBytes, sessionId, entry.SourcePath);
                }
                catch (Exception docxEx)
                {
                    _logger.LogWarning(docxEx, "Failed to load session {SessionId} from checkpoint/baseline; skipping.", sessionId);
                    continue;
                }

                // Replay patches after checkpoint (skip if WAL read failed)
                if (!walReadFailed && replayCount > checkpointPosition)
                {
                    var patchesToReplay = walEntries
                        .Skip(checkpointPosition)
                        .Take(replayCount - checkpointPosition)
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
                            _logger.LogWarning(ex, "Failed to replay WAL entry for session {SessionId}; stopping replay.",
                                sessionId);
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
                {
                    session.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore session {SessionId}; skipping.", sessionId);
            }
        }

        return restored;
    }

    // --- gRPC Storage Helpers ---

    private async Task<int> GetWalEntryCountAsync(string sessionId)
    {
        var (entries, _) = await _storage.ReadWalAsync(TenantId, sessionId);
        return entries.Count;
    }

    private async Task<List<WalEntry>> ReadWalEntriesAsync(string sessionId)
    {
        var (grpcEntries, _) = await _storage.ReadWalAsync(TenantId, sessionId);
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

        await _storage.AppendWalAsync(TenantId, sessionId, new[] { grpcEntry });
    }

    private async Task TruncateWalAtAsync(string sessionId, int keepCount)
    {
        await _storage.TruncateWalAsync(TenantId, sessionId, (ulong)keepCount);
    }

    // --- Source Management ---

    /// <summary>
    /// Set the source path for a session (for new documents or "Save As").
    /// If the session already has a source registered, updates it.
    /// </summary>
    public void SetSource(string id, string path, bool? autoSync = null)
    {
        var session = Get(id);
        var absolutePath = Path.GetFullPath(path);
        var effectiveAutoSync = autoSync ?? _autoSaveEnabled;

        try
        {
            // Check if source is already registered
            var status = _storage.GetSyncStatusAsync(TenantId, id).GetAwaiter().GetResult();

            if (status is not null)
            {
                // Update existing source
                var (success, error) = _storage.UpdateSourceAsync(
                    TenantId, id,
                    SourceType.LocalFile, absolutePath, effectiveAutoSync
                ).GetAwaiter().GetResult();

                if (!success)
                    throw new InvalidOperationException($"Failed to update source: {error}");

                _logger.LogInformation("Updated source for session {SessionId}: {Path} (auto_sync={AutoSync})",
                    id, absolutePath, effectiveAutoSync);
            }
            else
            {
                // Register new source
                var (success, error) = _storage.RegisterSourceAsync(
                    TenantId, id,
                    SourceType.LocalFile, absolutePath, effectiveAutoSync
                ).GetAwaiter().GetResult();

                if (!success)
                    throw new InvalidOperationException($"Failed to register source: {error}");

                _logger.LogInformation("Registered source for session {SessionId}: {Path} (auto_sync={AutoSync})",
                    id, absolutePath, effectiveAutoSync);
            }

            // Update in-memory session's source path
            session.SetSourcePath(absolutePath);

            // Update index with new source path
            // Note: This would require adding source_path update to the index proto
            // For now, we rely on the SourceSyncService registry
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set source for session {SessionId}.", id);
            throw;
        }
    }

    /// <summary>
    /// Get the sync status for a session.
    /// </summary>
    public SyncStatusDto? GetSyncStatus(string id)
    {
        Get(id); // Validate session exists
        return _storage.GetSyncStatusAsync(TenantId, id).GetAwaiter().GetResult();
    }

    // --- Private helpers ---

    private void MaybeAutoSave(string id)
    {
        if (!_autoSaveEnabled)
            return;

        try
        {
            var session = Get(id);

            // Check if source is registered with SourceSyncService
            var status = _storage.GetSyncStatusAsync(TenantId, id).GetAwaiter().GetResult();
            if (status is null || !status.AutoSyncEnabled)
            {
                // No source registered or auto-sync disabled - nothing to do
                return;
            }

            // Use gRPC SourceSyncService for auto-save
            var data = session.ToBytes();
            var (success, error, syncedAt) = _storage.SyncToSourceAsync(TenantId, id, data).GetAwaiter().GetResult();

            if (!success)
            {
                _logger.LogWarning("Auto-save failed for session {SessionId}: {Error}", id, error);
                return;
            }

            _externalChangeTracker?.UpdateSessionSnapshot(id);
            _logger.LogDebug("Auto-saved session {SessionId} to {Path} (synced_at={SyncedAt}).",
                id, status.Uri, syncedAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-save failed for session {SessionId}.", id);
        }
    }

    private async Task PersistNewSessionAsync(DocxSession session)
    {
        try
        {
            var bytes = session.ToBytes();
            await _storage.SaveSessionAsync(TenantId, session.Id, bytes);

            _cursors[session.Id] = 0;

            var now = DateTime.UtcNow;
            await _storage.AddSessionToIndexAsync(TenantId, session.Id,
                new Grpc.SessionIndexEntryDto(
                    session.SourcePath,
                    now,
                    now,
                    0,
                    Array.Empty<ulong>()));

            // Register source with SourceSyncService for auto-save via gRPC
            if (session.SourcePath is not null)
            {
                var (success, error) = await _storage.RegisterSourceAsync(
                    TenantId, session.Id,
                    SourceType.LocalFile, session.SourcePath, autoSync: _autoSaveEnabled);

                if (!success)
                {
                    _logger.LogWarning("Failed to register source for session {SessionId}: {Error}",
                        session.Id, error);
                }
                else
                {
                    _logger.LogDebug("Registered source for session {SessionId}: {Path}",
                        session.Id, session.SourcePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist new session {SessionId}.", session.Id);
        }
    }

    private async Task RebuildDocumentAtPositionAsync(string id, int targetPosition)
    {
        var checkpointPositions = await GetCheckpointPositionsAsync(id);

        // Try to load checkpoint
        var (ckptData, ckptPos, ckptFound) = await _storage.LoadCheckpointAsync(
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
            var (baselineData, _) = await _storage.LoadSessionAsync(TenantId, id);
            baseBytes = baselineData ?? throw new InvalidOperationException($"No baseline found for session {id}");
            checkpointPosition = 0;
        }

        var oldSession = Get(id);
        var newSession = DocxSession.FromBytes(baseBytes, oldSession.Id, oldSession.SourcePath);

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
    }

    private async Task MaybeCreateCheckpointAsync(string id, int newCursor)
    {
        if (newCursor > 0 && newCursor % _checkpointInterval == 0)
        {
            try
            {
                var session = Get(id);
                var bytes = session.ToBytes();
                await _storage.SaveCheckpointAsync(TenantId, id, (ulong)newCursor, bytes);

                await _storage.UpdateSessionInIndexAsync(TenantId, id,
                    addCheckpointPositions: new[] { (ulong)newCursor });

                _logger.LogInformation("Created checkpoint at position {Position} for session {SessionId}.", newCursor, id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create checkpoint at position {Position} for session {SessionId}.", newCursor, id);
            }
        }
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
