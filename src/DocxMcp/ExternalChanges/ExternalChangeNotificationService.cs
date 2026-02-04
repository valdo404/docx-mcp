using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocxMcp.ExternalChanges;

/// <summary>
/// Background service that monitors for external changes.
/// When an external change is detected, it automatically syncs the session
/// with the external file (same behavior as the CLI watch daemon).
/// </summary>
public sealed class ExternalChangeNotificationService : BackgroundService
{
    private readonly ExternalChangeTracker _tracker;
    private readonly SessionManager _sessions;
    private readonly ILogger<ExternalChangeNotificationService> _logger;

    public ExternalChangeNotificationService(
        ExternalChangeTracker tracker,
        SessionManager sessions,
        ILogger<ExternalChangeNotificationService> logger)
    {
        _tracker = tracker;
        _sessions = sessions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("External change notification service started.");

        // Subscribe to external change events
        _tracker.ExternalChangeDetected += OnExternalChangeDetected;

        // Start watching all existing sessions with source paths
        foreach (var (sessionId, sourcePath) in _sessions.List())
        {
            if (sourcePath is not null)
            {
                _tracker.StartWatching(sessionId);
            }
        }

        // Keep the service running
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            _tracker.ExternalChangeDetected -= OnExternalChangeDetected;
            _logger.LogInformation("External change notification service stopped.");
        }
    }

    private void OnExternalChangeDetected(object? sender, ExternalChangeDetectedEventArgs e)
    {
        try
        {
            _logger.LogInformation(
                "External change detected for session {SessionId}. Auto-syncing.",
                e.SessionId);

            var result = _tracker.SyncExternalChanges(e.SessionId, e.Patch.Id);

            if (result.HasChanges)
            {
                _logger.LogInformation(
                    "Auto-synced session {SessionId}: +{Added} -{Removed} ~{Modified}.",
                    e.SessionId,
                    result.Summary?.Added ?? 0,
                    result.Summary?.Removed ?? 0,
                    result.Summary?.Modified ?? 0);
            }
            else
            {
                _logger.LogDebug("No logical changes after sync for session {SessionId}.", e.SessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-sync external changes for session {SessionId}.", e.SessionId);
        }
    }
}
