using System.Collections.Concurrent;
using DocxMcp.Grpc;
using Microsoft.Extensions.Logging;

namespace DocxMcp;

/// <summary>
/// Thread-safe pool of SessionManagers, one per tenant.
/// Used only in HTTP mode for multi-tenant isolation.
/// Each SessionManager is created on first access (stateless — no session restore needed).
/// </summary>
public sealed class SessionManagerPool
{
    private readonly ConcurrentDictionary<string, SessionManager> _pool = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly IHistoryStorage _history;
    private readonly ILoggerFactory _loggerFactory;

    public SessionManagerPool(IHistoryStorage history, ILoggerFactory loggerFactory)
    {
        _history = history;
        _loggerFactory = loggerFactory;
    }

    public SessionManager GetForTenant(string tenantId)
    {
        if (_pool.TryGetValue(tenantId, out var existing))
            return existing;

        // Serialize creation per-tenant
        var @lock = _locks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        @lock.Wait();
        try
        {
            // Double-check after acquiring lock
            if (_pool.TryGetValue(tenantId, out existing))
                return existing;

            var sm = new SessionManager(_history, _loggerFactory.CreateLogger<SessionManager>(), tenantId);
            _pool[tenantId] = sm;
            return sm;
        }
        catch
        {
            // Don't cache failed managers — next request will retry
            _pool.TryRemove(tenantId, out _);
            throw;
        }
        finally
        {
            @lock.Release();
        }
    }
}
