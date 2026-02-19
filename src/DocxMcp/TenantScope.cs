using Microsoft.AspNetCore.Http;

namespace DocxMcp;

/// <summary>
/// Scoped service that resolves the correct SessionManager for the current request.
/// In stdio mode: wraps the singleton SessionManager.
/// In HTTP mode: reads X-Tenant-Id header and resolves from SessionManagerPool.
/// The .NET server does NO auth — X-Tenant-Id is injected by the upstream proxy.
/// </summary>
public sealed class TenantScope
{
    public string TenantId { get; }
    public SessionManager Sessions { get; }

    /// <summary>
    /// HTTP mode: resolve tenant from X-Tenant-Id header via SessionManagerPool.
    /// </summary>
    public TenantScope(IHttpContextAccessor accessor, SessionManagerPool pool)
    {
        TenantId = accessor.HttpContext?.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "";
        Sessions = pool.GetForTenant(TenantId);
    }

    /// <summary>
    /// Stdio mode: wrap the singleton SessionManager directly.
    /// </summary>
    public TenantScope(SessionManager sessions)
    {
        TenantId = sessions.TenantId;
        Sessions = sessions;
    }

    /// <summary>
    /// Implicit conversion from SessionManager (convenience for stdio mode and tests).
    /// </summary>
    public static implicit operator TenantScope(SessionManager sessions) => new(sessions);
}
