using DocxMcp.Grpc;
using Microsoft.Extensions.Logging;

namespace DocxMcp;

/// <summary>
/// Manages file synchronization and external watch lifecycle.
/// Independent from SessionManager — receives bytes from callers, not sessions.
/// </summary>
public sealed class SyncManager
{
    private readonly ISyncStorage _sync;
    private readonly ILogger<SyncManager> _logger;
    private readonly bool _autoSaveEnabled;

    public SyncManager(ISyncStorage sync, ILogger<SyncManager> logger)
    {
        _sync = sync;
        _logger = logger;

        var autoSaveEnv = Environment.GetEnvironmentVariable("DOCX_AUTO_SAVE");
        _autoSaveEnabled = autoSaveEnv is null || !string.Equals(autoSaveEnv, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Set or update the source path for a session. Registers or updates the source,
    /// then starts watching for external changes.
    /// </summary>
    public void SetSource(string tenantId, string sessionId, string path, bool autoSync)
    {
        var absolutePath = Path.GetFullPath(path);

        try
        {
            // Check if source is already registered
            var status = _sync.GetSyncStatusAsync(tenantId, sessionId).GetAwaiter().GetResult();

            if (status is not null)
            {
                // Update existing source
                var (success, error) = _sync.UpdateSourceAsync(
                    tenantId, sessionId,
                    SourceType.LocalFile, absolutePath, autoSync
                ).GetAwaiter().GetResult();

                if (!success)
                    throw new InvalidOperationException($"Failed to update source: {error}");

                _logger.LogInformation("Updated source for session {SessionId}: {Path} (auto_sync={AutoSync})",
                    sessionId, absolutePath, autoSync);
            }
            else
            {
                // Register new source
                var (success, error) = _sync.RegisterSourceAsync(
                    tenantId, sessionId,
                    SourceType.LocalFile, absolutePath, autoSync
                ).GetAwaiter().GetResult();

                if (!success)
                    throw new InvalidOperationException($"Failed to register source: {error}");

                _logger.LogInformation("Registered source for session {SessionId}: {Path} (auto_sync={AutoSync})",
                    sessionId, absolutePath, autoSync);
            }

            // Start watching for external changes
            try
            {
                var (watchSuccess, watchId, watchError) = _sync.StartWatchAsync(
                    tenantId, sessionId,
                    SourceType.LocalFile, absolutePath
                ).GetAwaiter().GetResult();

                if (watchSuccess)
                    _logger.LogDebug("Started external watch for session {SessionId}: watchId={WatchId}", sessionId, watchId);
                else
                    _logger.LogWarning("Failed to start external watch for session {SessionId}: {Error}", sessionId, watchError);
            }
            catch (Exception watchEx)
            {
                _logger.LogWarning(watchEx, "Exception starting external watch for session {SessionId}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set source for session {SessionId}.", sessionId);
            throw;
        }
    }

    /// <summary>
    /// Register a source and start watching. Used during Open and RestoreSessions.
    /// </summary>
    public void RegisterAndWatch(string tenantId, string sessionId, string path, bool autoSync)
    {
        var absolutePath = Path.GetFullPath(path);

        try
        {
            var (success, error) = _sync.RegisterSourceAsync(
                tenantId, sessionId,
                SourceType.LocalFile, absolutePath, autoSync
            ).GetAwaiter().GetResult();

            if (!success)
            {
                _logger.LogWarning("Failed to register source for session {SessionId}: {Error}", sessionId, error);
            }
            else
            {
                _logger.LogDebug("Registered source for session {SessionId}: {Path}", sessionId, absolutePath);
            }

            // Start watching
            try
            {
                var (watchSuccess, watchId, watchError) = _sync.StartWatchAsync(
                    tenantId, sessionId,
                    SourceType.LocalFile, absolutePath
                ).GetAwaiter().GetResult();

                if (watchSuccess)
                    _logger.LogDebug("Started external watch for session {SessionId}: watchId={WatchId}", sessionId, watchId);
                else
                    _logger.LogWarning("Failed to start external watch for session {SessionId}: {Error}", sessionId, watchError);
            }
            catch (Exception watchEx)
            {
                _logger.LogWarning(watchEx, "Exception starting external watch for session {SessionId}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register and watch for session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// Get the sync status for a session.
    /// </summary>
    public SyncStatusDto? GetSyncStatus(string tenantId, string sessionId)
    {
        return _sync.GetSyncStatusAsync(tenantId, sessionId).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Save session data to its registered source.
    /// </summary>
    public void Save(string tenantId, string sessionId, byte[] data)
    {
        var status = _sync.GetSyncStatusAsync(tenantId, sessionId).GetAwaiter().GetResult();
        if (status is null)
        {
            throw new InvalidOperationException(
                $"No save target registered for session '{sessionId}'. Use document_set_source to set a path first.");
        }

        var (success, error, _) = _sync.SyncToSourceAsync(tenantId, sessionId, data).GetAwaiter().GetResult();

        if (!success)
        {
            throw new InvalidOperationException($"Failed to save session '{sessionId}': {error}");
        }

        _logger.LogDebug("Saved session {SessionId} to {Path}.", sessionId, status.Uri);
    }

    /// <summary>
    /// Auto-save if enabled and source is registered with auto-sync.
    /// Returns true if auto-save was performed.
    /// </summary>
    public bool MaybeAutoSave(string tenantId, string sessionId, byte[] data)
    {
        if (!_autoSaveEnabled)
            return false;

        try
        {
            var status = _sync.GetSyncStatusAsync(tenantId, sessionId).GetAwaiter().GetResult();
            if (status is null || !status.AutoSyncEnabled)
                return false;

            var (success, error, syncedAt) = _sync.SyncToSourceAsync(tenantId, sessionId, data).GetAwaiter().GetResult();

            if (!success)
            {
                _logger.LogWarning("Auto-save failed for session {SessionId}: {Error}", sessionId, error);
                return false;
            }

            _logger.LogDebug("Auto-saved session {SessionId} to {Path} (synced_at={SyncedAt}).",
                sessionId, status.Uri, syncedAt);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-save failed for session {SessionId}.", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Stop watching a session's source file.
    /// </summary>
    public void StopWatch(string tenantId, string sessionId)
    {
        try
        {
            _sync.StopWatchAsync(tenantId, sessionId).GetAwaiter().GetResult();
            _logger.LogDebug("Stopped external watch for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stop external watch for session {SessionId} (may not have been watching)", sessionId);
        }
    }
}
