using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocxMcp;

/// <summary>
/// Restores persisted sessions on server startup by loading baselines and replaying WALs.
/// Re-registers watches for restored sessions with source paths.
/// </summary>
public sealed class SessionRestoreService : IHostedService
{
    private readonly SessionManager _sessions;
    private readonly SyncManager _sync;
    private readonly ILogger<SessionRestoreService> _logger;

    public SessionRestoreService(
        SessionManager sessions,
        SyncManager sync,
        ILogger<SessionRestoreService> logger)
    {
        _sessions = sessions;
        _sync = sync;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var restored = _sessions.RestoreSessions();
        if (restored > 0)
            _logger.LogInformation("Restored {Count} session(s) from storage.", restored);

        // Re-register watches for restored sessions with source paths
        foreach (var (sessionId, sourcePath) in _sessions.List())
        {
            if (sourcePath is not null)
                _sync.RegisterAndWatch(_sessions.TenantId, sessionId, sourcePath, autoSync: true);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
