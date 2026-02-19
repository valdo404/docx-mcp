namespace DocxMcp.Grpc;

/// <summary>
/// Helper class for managing tenant context in gRPC calls.
/// </summary>
public static class TenantContextHelper
{
    /// <summary>
    /// Default tenant ID for local CLI usage.
    /// Empty string for backward compatibility with legacy session paths
    /// (stores directly in sessions/ without tenant prefix).
    /// </summary>
    public const string LocalTenant = "";

    /// <summary>
    /// Default tenant ID for MCP stdio usage.
    /// Empty string for backward compatibility with legacy session paths.
    /// </summary>
    public const string DefaultTenant = "";

    /// <summary>
    /// Current tenant context stored as AsyncLocal for per-request isolation.
    /// </summary>
    private static readonly AsyncLocal<string?> _currentTenant = new();

    /// <summary>
    /// Get or set the current tenant ID.
    /// </summary>
    public static string CurrentTenantId
    {
        get => _currentTenant.Value ?? DefaultTenant;
        set => _currentTenant.Value = value;
    }

    /// <summary>
    /// Create a TenantContext protobuf message.
    /// </summary>
    public static TenantContext Create(string? tenantId = null)
    {
        return new TenantContext
        {
            TenantId = tenantId ?? CurrentTenantId
        };
    }

    /// <summary>
    /// Execute an action with a specific tenant context.
    /// </summary>
    public static T WithTenant<T>(string tenantId, Func<T> action)
    {
        var previous = _currentTenant.Value;
        try
        {
            _currentTenant.Value = tenantId;
            return action();
        }
        finally
        {
            _currentTenant.Value = previous;
        }
    }

    /// <summary>
    /// Execute an async action with a specific tenant context.
    /// </summary>
    public static async Task<T> WithTenantAsync<T>(string tenantId, Func<Task<T>> action)
    {
        var previous = _currentTenant.Value;
        try
        {
            _currentTenant.Value = tenantId;
            return await action();
        }
        finally
        {
            _currentTenant.Value = previous;
        }
    }
}
