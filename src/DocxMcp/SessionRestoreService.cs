using DocxMcp.ExternalChanges;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocxMcp;

/// <summary>
/// Restores persisted sessions on server startup by loading baselines and replaying WALs.
/// </summary>
public sealed class SessionRestoreService : IHostedService
{
    private readonly SessionManager _sessions;
    private readonly ExternalChangeTracker _externalChangeTracker;
    private readonly ILogger<SessionRestoreService> _logger;

    public SessionRestoreService(SessionManager sessions, ExternalChangeTracker externalChangeTracker, ILogger<SessionRestoreService> logger)
    {
        _sessions = sessions;
        _externalChangeTracker = externalChangeTracker;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.SetExternalChangeTracker(_externalChangeTracker);
        var restored = _sessions.RestoreSessions();
        if (restored > 0)
            _logger.LogInformation("Restored {Count} session(s) from storage.", restored);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
