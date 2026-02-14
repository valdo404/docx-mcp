using System.Collections.Concurrent;
using DocxMcp.Grpc;
using Microsoft.Extensions.Logging;

namespace DocxMcp;

/// <summary>
/// Thread-safe pool of SessionManagers, one per tenant.
/// Used only in HTTP mode for multi-tenant isolation.
/// Each SessionManager is lazy-created on first access.
/// </summary>
public sealed class SessionManagerPool
{
    private readonly ConcurrentDictionary<string, Lazy<SessionManager>> _pool = new();
    private readonly IHistoryStorage _history;
    private readonly ILoggerFactory _loggerFactory;

    public SessionManagerPool(IHistoryStorage history, ILoggerFactory loggerFactory)
    {
        _history = history;
        _loggerFactory = loggerFactory;
    }

    public SessionManager GetForTenant(string tenantId)
    {
        return _pool.GetOrAdd(tenantId, tid =>
            new Lazy<SessionManager>(() =>
                new SessionManager(_history, _loggerFactory.CreateLogger<SessionManager>(), tid)
            )).Value;
    }
}
