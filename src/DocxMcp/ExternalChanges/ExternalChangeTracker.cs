using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocxMcp.Diff;
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
    /// Manually check for external changes (polling fallback).
    /// </summary>
    public ExternalChangePatch? CheckForChanges(string sessionId)
    {
        if (!_watchedSessions.TryGetValue(sessionId, out var watched))
        {
            // Not being watched, start watching and check
            StartWatching(sessionId);
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

    private void OnFileChanged(string sessionId, string filePath)
    {
        // Debounce: wait a bit for file to be fully written
        Task.Delay(500).ContinueWith(_ =>
        {
            try
            {
                if (_watchedSessions.TryGetValue(sessionId, out var watched))
                {
                    var patch = DetectAndGeneratePatch(watched);
                    if (patch is not null)
                    {
                        RaiseExternalChangeDetected(sessionId, patch);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing file change for session {SessionId}.", sessionId);
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
                if (!File.Exists(watched.SourcePath))
                {
                    _logger.LogWarning("Source file no longer exists: {Path}", watched.SourcePath);
                    return null;
                }

                // Check if file has actually changed
                var currentHash = ComputeFileHash(watched.SourcePath);
                if (currentHash == watched.LastKnownHash)
                {
                    return null; // No change
                }

                _logger.LogInformation("External change detected for session {SessionId}. Previous hash: {Old}, New hash: {New}",
                    watched.SessionId, watched.LastKnownHash, currentHash);

                // Read the external file
                var externalBytes = File.ReadAllBytes(watched.SourcePath);

                // Compare with session snapshot
                var diff = DiffEngine.Compare(watched.SessionSnapshot, externalBytes);

                if (!diff.HasChanges)
                {
                    // File changed but no logical diff (maybe just metadata)
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
