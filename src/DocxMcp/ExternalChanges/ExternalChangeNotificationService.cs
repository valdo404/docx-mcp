using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocxMcp.ExternalChanges;

/// <summary>
/// Background service that monitors for external changes.
/// When an external change is detected, it stores the change and logs a warning.
/// Patch operations will block until changes are acknowledged.
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

    private async void OnExternalChangeDetected(object? sender, ExternalChangeDetectedEventArgs e)
    {
        try
        {
            _logger.LogInformation(
                "External change detected for session {SessionId}. Sending MCP notification.",
                e.SessionId);

            // Send MCP resource update notification
            // This tells the client that the resource has changed and needs to be re-read
            await SendResourceUpdateNotificationAsync(e.SessionId, e.Patch);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send MCP notification for external change.");
        }
    }

    private async Task SendResourceUpdateNotificationAsync(string sessionId, ExternalChangePatch patch)
    {
        try
        {
            // Create a notification message that the LLM will see
            // The notification includes a summary that encourages reading the full change
            var notification = new
            {
                type = "external_document_change",
                session_id = sessionId,
                change_id = patch.Id,
                detected_at = patch.DetectedAt,
                summary = new
                {
                    total_changes = patch.Summary.TotalChanges,
                    added = patch.Summary.Added,
                    removed = patch.Summary.Removed,
                    modified = patch.Summary.Modified,
                    moved = patch.Summary.Moved
                },
                message = $"ATTENTION: The document '{Path.GetFileName(patch.SourcePath)}' has been modified externally. " +
                          $"{patch.Summary.TotalChanges} change(s) detected. " +
                          "Call `get_external_changes` with acknowledge=true to proceed.",
                required_action = "Call get_external_changes with acknowledge=true before any further edits."
            };

            // Log the notification (MCP server will handle actual notification delivery)
            _logger.LogWarning(
                "EXTERNAL CHANGE NOTIFICATION - Session: {SessionId}, Changes: {Count}, " +
                "Action Required: Review and acknowledge before editing.",
                sessionId, patch.Summary.TotalChanges);

            // Note: The actual MCP notification mechanism depends on the MCP SDK implementation.
            // The current ModelContextProtocol.Server SDK may not expose direct notification APIs.
            // In that case, tools that modify the document should check for pending changes first.

            // For now, we ensure the patch is stored and tools can query it
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send resource update notification for session {SessionId}.", sessionId);
        }
    }
}
