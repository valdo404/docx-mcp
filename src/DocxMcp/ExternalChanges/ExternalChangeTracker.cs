using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DocxMcp.Diff;
using DocxMcp.Helpers;
using DocxMcp.Persistence;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;

namespace DocxMcp.ExternalChanges;

/// <summary>
/// Tracks external modifications to source files and generates logical patches.
/// Uses FileSystemWatcher for real-time detection with polling fallback.
/// </summary>
public sealed class ExternalChangeTracker : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly ILogger<ExternalChangeTracker> _logger;
    private readonly ConcurrentDictionary<string, WatchedSession> _watchedSessions = new();
    private readonly ConcurrentDictionary<string, List<ExternalChangePatch>> _pendingChanges = new();
    private readonly object _lock = new();

    /// <summary>
    /// Enable debug logging via DEBUG environment variable.
    /// </summary>
    private static bool DebugEnabled =>
        Environment.GetEnvironmentVariable("DEBUG") is not null;

    /// <summary>
    /// Event raised when an external change is detected.
    /// </summary>
    public event EventHandler<ExternalChangeDetectedEventArgs>? ExternalChangeDetected;

    public ExternalChangeTracker(SessionManager sessions, ILogger<ExternalChangeTracker> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Start watching a session's source file for external changes.
    /// </summary>
    public void StartWatching(string sessionId)
    {
        try
        {
            var session = _sessions.Get(sessionId);
            if (session.SourcePath is null)
            {
                _logger.LogDebug("Session {SessionId} has no source path, skipping watch.", sessionId);
                return;
            }

            if (!File.Exists(session.SourcePath))
            {
                _logger.LogWarning("Source file not found for session {SessionId}: {Path}",
                    sessionId, session.SourcePath);
                return;
            }

            if (_watchedSessions.ContainsKey(sessionId))
            {
                _logger.LogDebug("Session {SessionId} is already being watched.", sessionId);
                return;
            }

            var watched = new WatchedSession
            {
                SessionId = sessionId,
                SourcePath = session.SourcePath,
                LastKnownHash = ComputeFileHash(session.SourcePath),
                LastKnownSize = new FileInfo(session.SourcePath).Length,
                LastChecked = DateTime.UtcNow,
                SessionSnapshot = session.ToBytes()
            };

            // Create FileSystemWatcher
            var directory = Path.GetDirectoryName(session.SourcePath)!;
            var fileName = Path.GetFileName(session.SourcePath);

            watched.Watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            watched.Watcher.Changed += (_, e) => OnFileChanged(sessionId, e.FullPath);
            watched.Watcher.Renamed += (_, e) => OnFileRenamed(sessionId, e.OldFullPath, e.FullPath);

            _watchedSessions[sessionId] = watched;
            _pendingChanges[sessionId] = [];

            _logger.LogInformation("Started watching session {SessionId} source file: {Path}",
                sessionId, session.SourcePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start watching session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// Stop watching a session's source file.
    /// </summary>
    public void StopWatching(string sessionId)
    {
        if (_watchedSessions.TryRemove(sessionId, out var watched))
        {
            watched.Watcher?.Dispose();
            _logger.LogInformation("Stopped watching session {SessionId}.", sessionId);
        }
        _pendingChanges.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Update the session snapshot after applying changes (e.g., after save).
    /// </summary>
    public void UpdateSessionSnapshot(string sessionId)
    {
        if (_watchedSessions.TryGetValue(sessionId, out var watched))
        {
            try
            {
                var session = _sessions.Get(sessionId);
                watched.SessionSnapshot = session.ToBytes();
                watched.LastKnownHash = ComputeFileHash(watched.SourcePath);
                watched.LastKnownSize = new FileInfo(watched.SourcePath).Length;
                watched.LastChecked = DateTime.UtcNow;

                _logger.LogDebug("Updated session snapshot for {SessionId}.", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update session snapshot for {SessionId}.", sessionId);
            }
        }
    }

    /// <summary>
    /// Register a session for tracking without creating a FileSystemWatcher.
    /// Use this when an external component (e.g., WatchDaemon) manages the FSW.
    /// </summary>
    public void EnsureTracked(string sessionId)
    {
        if (_watchedSessions.ContainsKey(sessionId))
            return;

        try
        {
            var session = _sessions.Get(sessionId);
            if (session.SourcePath is null || !File.Exists(session.SourcePath))
                return;

            var watched = new WatchedSession
            {
                SessionId = sessionId,
                SourcePath = session.SourcePath,
                LastKnownHash = ComputeFileHash(session.SourcePath),
                LastKnownSize = new FileInfo(session.SourcePath).Length,
                LastChecked = DateTime.UtcNow,
                SessionSnapshot = session.ToBytes()
            };

            _watchedSessions[sessionId] = watched;
            _pendingChanges[sessionId] = [];

            if (DebugEnabled)
                Console.Error.WriteLine($"[DEBUG:tracker] Registered session {sessionId} for tracking (no FSW)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register session {SessionId} for tracking.", sessionId);
        }
    }

    /// <summary>
    /// Manually check for external changes (polling fallback).
    /// </summary>
    public ExternalChangePatch? CheckForChanges(string sessionId)
    {
        if (DebugEnabled)
            Console.Error.WriteLine($"[DEBUG:tracker] CheckForChanges called for session {sessionId}");

        if (!_watchedSessions.TryGetValue(sessionId, out var watched))
        {
            if (DebugEnabled)
                Console.Error.WriteLine($"[DEBUG:tracker] Session not tracked, registering without FSW");
            // Not being tracked, register without FSW and check
            EnsureTracked(sessionId);
            if (!_watchedSessions.TryGetValue(sessionId, out watched))
                return null;
        }

        return DetectAndGeneratePatch(watched);
    }

    /// <summary>
    /// Get pending external changes for a session.
    /// </summary>
    public PendingExternalChanges GetPendingChanges(string sessionId)
    {
        var changes = _pendingChanges.GetOrAdd(sessionId, _ => []);
        return new PendingExternalChanges
        {
            SessionId = sessionId,
            Changes = changes.OrderByDescending(c => c.DetectedAt).ToList()
        };
    }

    /// <summary>
    /// Get the most recent unacknowledged change for a session.
    /// </summary>
    public ExternalChangePatch? GetLatestUnacknowledgedChange(string sessionId)
    {
        if (_pendingChanges.TryGetValue(sessionId, out var changes))
        {
            return changes
                .Where(c => !c.Acknowledged)
                .OrderByDescending(c => c.DetectedAt)
                .FirstOrDefault();
        }
        return null;
    }

    /// <summary>
    /// Check if a session has pending unacknowledged changes.
    /// </summary>
    public bool HasPendingChanges(string sessionId)
    {
        return GetLatestUnacknowledgedChange(sessionId) is not null;
    }

    /// <summary>
    /// Acknowledge an external change, allowing the LLM to continue editing.
    /// </summary>
    public bool AcknowledgeChange(string sessionId, string changeId)
    {
        if (_pendingChanges.TryGetValue(sessionId, out var changes))
        {
            var change = changes.FirstOrDefault(c => c.Id == changeId);
            if (change is not null)
            {
                change.Acknowledged = true;
                change.AcknowledgedAt = DateTime.UtcNow;

                _logger.LogInformation("External change {ChangeId} acknowledged for session {SessionId}.",
                    changeId, sessionId);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Acknowledge all pending changes for a session.
    /// </summary>
    public int AcknowledgeAllChanges(string sessionId)
    {
        int count = 0;
        if (_pendingChanges.TryGetValue(sessionId, out var changes))
        {
            foreach (var change in changes.Where(c => !c.Acknowledged))
            {
                change.Acknowledged = true;
                change.AcknowledgedAt = DateTime.UtcNow;
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Synchronize the session with external file changes.
    /// This is the full sync workflow:
    /// 1. Reload document from disk (store FULL bytes in WAL)
    /// 2. Re-assign ALL dmcp:ids
    /// 3. Detect uncovered changes (headers, images, etc.)
    /// 4. Create WAL entry with full document snapshot
    /// 5. Force checkpoint
    /// 6. Replace in-memory session
    /// </summary>
    /// <param name="sessionId">Session ID to sync.</param>
    /// <param name="changeId">Optional change ID to acknowledge.</param>
    /// <returns>Result of the sync operation.</returns>
    public SyncResult SyncExternalChanges(string sessionId, string? changeId = null, bool isImport = false)
    {
        lock (_lock)
        {
            try
            {
                var session = _sessions.Get(sessionId);
                if (session.SourcePath is null)
                    return SyncResult.Failure("Session has no source path. Cannot sync.");

                if (!File.Exists(session.SourcePath))
                    return SyncResult.Failure($"Source file not found: {session.SourcePath}");

                if (DebugEnabled)
                    Console.Error.WriteLine($"[DEBUG:sync] Starting sync for session {sessionId}");

                // 1. Read external file (store FULL bytes)
                var newBytes = File.ReadAllBytes(session.SourcePath);
                var previousBytes = session.ToBytes();

                // 2. Compute CONTENT hashes (ignoring IDs) for change detection
                // This prevents duplicate WAL entries when only ID attributes differ
                var previousContentHash = ContentHasher.ComputeContentHash(previousBytes);
                var newContentHash = ContentHasher.ComputeContentHash(newBytes);

                if (DebugEnabled)
                {
                    Console.Error.WriteLine($"[DEBUG:sync] Previous content hash: {previousContentHash}");
                    Console.Error.WriteLine($"[DEBUG:sync] New content hash:      {newContentHash}");
                }

                if (previousContentHash == newContentHash)
                {
                    if (DebugEnabled)
                        Console.Error.WriteLine($"[DEBUG:sync] Content unchanged, skipping sync");
                    return SyncResult.NoChanges();
                }

                // 3. Compute full byte hashes for WAL metadata (for debugging/auditing)
                var previousHash = ComputeBytesHash(previousBytes);
                var newHash = ComputeBytesHash(newBytes);

                if (DebugEnabled)
                {
                    Console.Error.WriteLine($"[DEBUG:sync] Content changed, proceeding with sync");
                    Console.Error.WriteLine($"[DEBUG:sync] Previous bytes hash: {previousHash}");
                    Console.Error.WriteLine($"[DEBUG:sync] New bytes hash:      {newHash}");
                }

                _logger.LogInformation(
                    "Syncing external changes for session {SessionId}. Previous hash: {Old}, New hash: {New}",
                    sessionId, previousHash, newHash);

                // 3. Open new document and detect changes BEFORE replacing session
                List<UncoveredChange> uncoveredChanges;
                DiffResult diff;

                using (var newStream = new MemoryStream(newBytes))
                using (var newDoc = WordprocessingDocument.Open(newStream, isEditable: false))
                {
                    // Detect uncovered changes (headers, footers, images, etc.)
                    uncoveredChanges = DiffEngine.DetectUncoveredChanges(session.Document, newDoc);

                    // Detect body changes
                    diff = DiffEngine.Compare(previousBytes, newBytes);
                }

                // 4. Create new session with re-assigned IDs
                var newSession = DocxSession.FromBytes(newBytes, session.Id, session.SourcePath);
                ElementIdManager.EnsureNamespace(newSession.Document);
                ElementIdManager.EnsureAllIds(newSession.Document);

                // Get updated bytes after ID assignment
                var finalBytes = newSession.ToBytes();

                // 5. Build WAL entry with FULL document snapshot
                var walEntry = new WalEntry
                {
                    EntryType = isImport ? WalEntryType.Import : WalEntryType.ExternalSync,
                    Timestamp = DateTime.UtcNow,
                    Patches = JsonSerializer.Serialize(diff.ToPatches(), DocxMcp.Models.DocxJsonContext.Default.ListJsonObject),
                    Description = BuildSyncDescription(diff.Summary, uncoveredChanges),
                    SyncMeta = new ExternalSyncMeta
                    {
                        SourcePath = session.SourcePath,
                        PreviousHash = previousHash,
                        NewHash = newHash,
                        Summary = diff.Summary,
                        UncoveredChanges = uncoveredChanges,
                        DocumentSnapshot = finalBytes
                    }
                };

                // 6. Append to WAL + checkpoint + replace session
                var walPosition = _sessions.AppendExternalSync(sessionId, walEntry, newSession);

                // 7. Update watched session state
                if (_watchedSessions.TryGetValue(sessionId, out var watched))
                {
                    watched.LastKnownHash = newHash;
                    watched.SessionSnapshot = finalBytes;
                    watched.LastChecked = DateTime.UtcNow;
                }

                // 8. Acknowledge change if specified
                if (changeId is not null)
                    AcknowledgeChange(sessionId, changeId);

                _logger.LogInformation(
                    "External sync completed for session {SessionId}. Body: +{Added} -{Removed} ~{Modified}. Uncovered: {Uncovered}",
                    sessionId, diff.Summary.Added, diff.Summary.Removed, diff.Summary.Modified, uncoveredChanges.Count);

                return SyncResult.Synced(diff.Summary, uncoveredChanges, diff.ToPatches(), changeId, walPosition);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync external changes for session {SessionId}.", sessionId);
                return SyncResult.Failure($"Sync failed: {ex.Message}");
            }
        }
    }

    private static string BuildSyncDescription(DiffSummary summary, List<UncoveredChange> uncovered)
    {
        var parts = new List<string> { "[EXTERNAL SYNC]" };

        if (summary.TotalChanges > 0)
            parts.Add($"+{summary.Added} -{summary.Removed} ~{summary.Modified}");
        else
            parts.Add("no body changes");

        if (uncovered.Count > 0)
        {
            var types = uncovered
                .Select(u => u.Type.ToString().ToLowerInvariant())
                .Distinct()
                .Take(3);
            parts.Add($"({uncovered.Count} uncovered: {string.Join(", ", types)})");
        }

        return string.Join(" ", parts);
    }

    private static string ComputeBytesHash(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void OnFileChanged(string sessionId, string filePath)
    {
        if (DebugEnabled)
            Console.Error.WriteLine($"[DEBUG:tracker] FSW fired for {Path.GetFileName(filePath)} (session {sessionId})");

        // Debounce: wait a bit for file to be fully written
        Task.Delay(500).ContinueWith(_ =>
        {
            try
            {
                if (DebugEnabled)
                    Console.Error.WriteLine($"[DEBUG:tracker] Processing FSW event after 500ms debounce");

                if (_watchedSessions.TryGetValue(sessionId, out var watched))
                {
                    var patch = DetectAndGeneratePatch(watched);
                    if (patch is not null)
                    {
                        if (DebugEnabled)
                            Console.Error.WriteLine($"[DEBUG:tracker] Change detected, raising event (patch={patch.Id})");
                        RaiseExternalChangeDetected(sessionId, patch);
                    }
                    else if (DebugEnabled)
                    {
                        Console.Error.WriteLine($"[DEBUG:tracker] No changes detected after FSW event");
                    }
                }
                else if (DebugEnabled)
                {
                    Console.Error.WriteLine($"[DEBUG:tracker] Session {sessionId} not in watched sessions");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing file change for session {SessionId}.", sessionId);
                if (DebugEnabled)
                    Console.Error.WriteLine($"[DEBUG:tracker] Exception in OnFileChanged: {ex}");
            }
        });
    }

    private void OnFileRenamed(string sessionId, string oldPath, string newPath)
    {
        _logger.LogWarning("Source file for session {SessionId} was renamed from {OldPath} to {NewPath}.",
            sessionId, oldPath, newPath);

        // Update the watched path
        if (_watchedSessions.TryGetValue(sessionId, out var watched))
        {
            watched.SourcePath = newPath;
        }
    }

    private ExternalChangePatch? DetectAndGeneratePatch(WatchedSession watched)
    {
        lock (_lock)
        {
            try
            {
                if (DebugEnabled)
                    Console.Error.WriteLine($"[DEBUG:tracker] DetectAndGeneratePatch for {Path.GetFileName(watched.SourcePath)}");

                if (!File.Exists(watched.SourcePath))
                {
                    if (DebugEnabled)
                        Console.Error.WriteLine($"[DEBUG:tracker] Source file does not exist: {watched.SourcePath}");
                    _logger.LogWarning("Source file no longer exists: {Path}", watched.SourcePath);
                    return null;
                }

                // Check if file has actually changed
                var currentHash = ComputeFileHash(watched.SourcePath);
                if (DebugEnabled)
                    Console.Error.WriteLine($"[DEBUG:tracker] File hash: {currentHash}, Last known: {watched.LastKnownHash}");
                if (currentHash == watched.LastKnownHash)
                {
                    if (DebugEnabled)
                        Console.Error.WriteLine($"[DEBUG:tracker] Hash unchanged, no changes");
                    return null; // No change
                }

                _logger.LogInformation("External change detected for session {SessionId}. Previous hash: {Old}, New hash: {New}",
                    watched.SessionId, watched.LastKnownHash, currentHash);

                // Read the external file
                var externalBytes = File.ReadAllBytes(watched.SourcePath);

                // Compare with session snapshot
                var diff = DiffEngine.Compare(watched.SessionSnapshot, externalBytes);

                if (DebugEnabled)
                    Console.Error.WriteLine($"[DEBUG:tracker] Diff result: HasChanges={diff.HasChanges}, HasAnyChanges={diff.HasAnyChanges}, Changes={diff.Changes.Count}, Uncovered={diff.UncoveredChanges.Count}");

                if (!diff.HasChanges)
                {
                    // File changed but no logical diff (maybe just metadata)
                    if (DebugEnabled)
                        Console.Error.WriteLine($"[DEBUG:tracker] No body changes, updating hash only");
                    watched.LastKnownHash = currentHash;
                    watched.LastChecked = DateTime.UtcNow;
                    return null;
                }

                // Generate the external change patch
                var patch = new ExternalChangePatch
                {
                    Id = $"ext_{watched.SessionId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}",
                    SessionId = watched.SessionId,
                    DetectedAt = DateTime.UtcNow,
                    SourcePath = watched.SourcePath,
                    PreviousHash = watched.LastKnownHash,
                    NewHash = currentHash,
                    Summary = diff.Summary,
                    Changes = diff.Changes.Select(ExternalElementChange.FromElementChange).ToList(),
                    Patches = diff.ToPatches()
                };

                // Store in pending changes
                if (_pendingChanges.TryGetValue(watched.SessionId, out var changes))
                {
                    changes.Add(patch);
                }

                // Update watched state
                watched.LastKnownHash = currentHash;
                watched.LastChecked = DateTime.UtcNow;

                _logger.LogInformation("Generated external change patch {PatchId} for session {SessionId}: {Summary}",
                    patch.Id, watched.SessionId, $"{diff.Summary.TotalChanges} changes");

                return patch;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate external change patch for session {SessionId}.",
                    watched.SessionId);
                return null;
            }
        }
    }

    private void RaiseExternalChangeDetected(string sessionId, ExternalChangePatch patch)
    {
        try
        {
            ExternalChangeDetected?.Invoke(this, new ExternalChangeDetectedEventArgs
            {
                SessionId = sessionId,
                Patch = patch
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in ExternalChangeDetected event handler.");
        }
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        foreach (var watched in _watchedSessions.Values)
        {
            watched.Watcher?.Dispose();
        }
        _watchedSessions.Clear();
        _pendingChanges.Clear();
    }

    private sealed class WatchedSession
    {
        public required string SessionId { get; init; }
        public required string SourcePath { get; set; }
        public required string LastKnownHash { get; set; }
        public required long LastKnownSize { get; set; }
        public required DateTime LastChecked { get; set; }
        public required byte[] SessionSnapshot { get; set; }
        public FileSystemWatcher? Watcher { get; set; }
    }
}

/// <summary>
/// Event args for external change detection.
/// </summary>
public sealed class ExternalChangeDetectedEventArgs : EventArgs
{
    public required string SessionId { get; init; }
    public required ExternalChangePatch Patch { get; init; }
}
