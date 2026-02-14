using DocxMcp.Grpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocxMcp.Tests;

internal static class TestHelpers
{
    private static IHistoryStorage? _sharedHistoryStorage;
    private static ISyncStorage? _sharedSyncStorage;
    private static readonly object _lock = new();
    private static string? _testStorageDir;

    /// <summary>
    /// Create a SessionManager backed by the gRPC storage server.
    /// Auto-launches the Rust storage server if not already running.
    /// Uses a unique tenant ID per test to ensure isolation.
    /// </summary>
    public static SessionManager CreateSessionManager()
    {
        var historyStorage = GetOrCreateHistoryStorage();

        // Use unique tenant per test for isolation
        var tenantId = $"test-{Guid.NewGuid():N}";

        return new SessionManager(historyStorage, NullLogger<SessionManager>.Instance, tenantId);
    }

    /// <summary>
    /// Create a SessionManager with a specific tenant ID (for multi-tenant tests).
    /// The tenant ID is captured at construction time, ensuring thread-safety
    /// even when used across parallel operations.
    /// </summary>
    public static SessionManager CreateSessionManager(string tenantId)
    {
        var historyStorage = GetOrCreateHistoryStorage();
        return new SessionManager(historyStorage, NullLogger<SessionManager>.Instance, tenantId);
    }

    /// <summary>
    /// Create a SyncManager backed by the gRPC sync storage.
    /// </summary>
    public static SyncManager CreateSyncManager()
    {
        var syncStorage = GetOrCreateSyncStorage();
        return new SyncManager(syncStorage, NullLogger<SyncManager>.Instance);
    }

    /// <summary>
    /// Get or create a shared history storage client.
    /// The Rust gRPC server is auto-launched via Unix socket if not running.
    /// </summary>
    public static IHistoryStorage GetOrCreateHistoryStorage()
    {
        if (_sharedHistoryStorage != null)
            return _sharedHistoryStorage;

        lock (_lock)
        {
            if (_sharedHistoryStorage != null)
                return _sharedHistoryStorage;

            EnsureStorageInitialized();
            return _sharedHistoryStorage!;
        }
    }

    /// <summary>
    /// Get or create a shared sync storage client.
    /// </summary>
    public static ISyncStorage GetOrCreateSyncStorage()
    {
        if (_sharedSyncStorage != null)
            return _sharedSyncStorage;

        lock (_lock)
        {
            if (_sharedSyncStorage != null)
                return _sharedSyncStorage;

            EnsureStorageInitialized();
            return _sharedSyncStorage!;
        }
    }

    private static void EnsureStorageInitialized()
    {
        if (_sharedHistoryStorage != null && _sharedSyncStorage != null)
            return;

        // Use a temporary directory for test isolation
        _testStorageDir = Path.Combine(Path.GetTempPath(), $"docx-mcp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testStorageDir);

        var options = StorageClientOptions.FromEnvironment();
        options.LocalStorageDir = _testStorageDir;

        if (!string.IsNullOrEmpty(options.ServerUrl))
        {
            // Dual-server mode: history → remote STORAGE_GRPC_URL, sync → local embedded
            var remoteChannel = HistoryStorageClient.CreateChannelAsync(options, launcher: null)
                .GetAwaiter().GetResult();
            _sharedHistoryStorage = new HistoryStorageClient(remoteChannel, NullLogger<HistoryStorageClient>.Instance);

            // Local embedded server for sync (always local file operations)
            var localOptions = new StorageClientOptions { LocalStorageDir = _testStorageDir };
            var localLauncher = new GrpcLauncher(localOptions, NullLogger<GrpcLauncher>.Instance);
            var localChannel = HistoryStorageClient.CreateChannelAsync(localOptions, localLauncher)
                .GetAwaiter().GetResult();
            _sharedSyncStorage = new SyncStorageClient(localChannel, NullLogger<SyncStorageClient>.Instance);
        }
        else
        {
            // Embedded mode: single local server for both
            var launcher = new GrpcLauncher(options, NullLogger<GrpcLauncher>.Instance);
            var channel = HistoryStorageClient.CreateChannelAsync(options, launcher)
                .GetAwaiter().GetResult();
            _sharedHistoryStorage = new HistoryStorageClient(channel, NullLogger<HistoryStorageClient>.Instance);
            _sharedSyncStorage = new SyncStorageClient(channel, NullLogger<SyncStorageClient>.Instance);
        }
    }

    /// <summary>
    /// Cleanup: dispose the shared storage clients and remove temp directory.
    /// Call this in test cleanup if needed.
    /// </summary>
    public static async Task DisposeStorageAsync()
    {
        if (_sharedHistoryStorage != null)
        {
            await _sharedHistoryStorage.DisposeAsync();
            _sharedHistoryStorage = null;
        }

        if (_sharedSyncStorage != null)
        {
            await _sharedSyncStorage.DisposeAsync();
            _sharedSyncStorage = null;
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
