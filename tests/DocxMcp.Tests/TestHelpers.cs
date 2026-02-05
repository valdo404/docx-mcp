using DocxMcp.Grpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocxMcp.Tests;

internal static class TestHelpers
{
    private static IStorageClient? _sharedStorage;
    private static readonly object _lock = new();
    private static string? _testStorageDir;

    /// <summary>
    /// Create a SessionManager backed by the gRPC storage server.
    /// Auto-launches the Rust storage server if not already running.
    /// Uses a unique tenant ID per test to ensure isolation.
    /// </summary>
    public static SessionManager CreateSessionManager()
    {
        var storage = GetOrCreateStorageClient();

        // Use unique tenant per test for isolation
        var tenantId = $"test-{Guid.NewGuid():N}";

        return new SessionManager(storage, NullLogger<SessionManager>.Instance, tenantId);
    }

    /// <summary>
    /// Create a SessionManager with a specific tenant ID (for multi-tenant tests).
    /// The tenant ID is captured at construction time, ensuring thread-safety
    /// even when used across parallel operations.
    /// </summary>
    public static SessionManager CreateSessionManager(string tenantId)
    {
        var storage = GetOrCreateStorageClient();
        return new SessionManager(storage, NullLogger<SessionManager>.Instance, tenantId);
    }

    /// <summary>
    /// Get or create a shared storage client.
    /// The Rust gRPC server is auto-launched via Unix socket if not running.
    /// Reads configuration from environment variables (STORAGE_SERVER_PATH, etc.).
    /// </summary>
    public static IStorageClient GetOrCreateStorageClient()
    {
        if (_sharedStorage != null)
            return _sharedStorage;

        lock (_lock)
        {
            if (_sharedStorage != null)
                return _sharedStorage;

            // Use a temporary directory for test isolation
            _testStorageDir = Path.Combine(Path.GetTempPath(), $"docx-mcp-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testStorageDir);

            var options = StorageClientOptions.FromEnvironment();
            options.LocalStorageDir = _testStorageDir;

            var launcher = new GrpcLauncher(options, NullLogger<GrpcLauncher>.Instance);
            _sharedStorage = StorageClient.CreateAsync(options, launcher, NullLogger<StorageClient>.Instance)
                .GetAwaiter().GetResult();

            return _sharedStorage;
        }
    }

    /// <summary>
    /// Cleanup: dispose the shared storage client and remove temp directory.
    /// Call this in test cleanup if needed.
    /// </summary>
    public static async Task DisposeStorageAsync()
    {
        if (_sharedStorage != null)
        {
            await _sharedStorage.DisposeAsync();
            _sharedStorage = null;
        }

        // Clean up temp directory
        if (_testStorageDir != null && Directory.Exists(_testStorageDir))
        {
            try
            {
                Directory.Delete(_testStorageDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
            _testStorageDir = null;
        }
    }
}
