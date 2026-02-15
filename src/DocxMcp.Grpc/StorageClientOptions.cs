namespace DocxMcp.Grpc;

/// <summary>
/// Configuration options for the gRPC storage client.
/// </summary>
public sealed class StorageClientOptions
{
    /// <summary>
    /// gRPC server URL for history storage (e.g., "http://localhost:50051").
    /// If null, auto-launch mode uses Unix socket.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// gRPC server URL for sync/watch storage (e.g., "http://localhost:50052").
    /// If null, uses local embedded sync via InMemoryPipeStream.
    /// Used for remote sync backends like Google Drive.
    /// </summary>
    public string? SyncServerUrl { get; set; }

    /// <summary>
    /// Path to Unix socket (e.g., "/tmp/docx-storage-local.sock").
    /// Used when ServerUrl is null and on Unix-like systems.
    /// </summary>
    public string? UnixSocketPath { get; set; }

    /// <summary>
    /// Whether to auto-launch the gRPC server if not running.
    /// Only applies when ServerUrl is null.
    /// </summary>
    public bool AutoLaunch { get; set; } = true;

    /// <summary>
    /// Path to the storage server binary for auto-launch.
    /// If null, searches in PATH or relative to current assembly.
    /// </summary>
    public string? StorageServerPath { get; set; }

    /// <summary>
    /// Base directory for local storage.
    /// Passed to the storage server via --local-storage-dir.
    /// The server will create {base}/{tenant_id}/sessions/ structure.
    /// Default: LocalApplicationData/docx-mcp
    /// </summary>
    public string? LocalStorageDir { get; set; }

    /// <summary>
    /// Get effective local storage directory.
    /// Note: This returns the BASE directory, not the sessions directory.
    /// The storage server adds {tenant_id}/sessions/ to this path.
    /// </summary>
    public string GetEffectiveLocalStorageDir()
    {
        if (LocalStorageDir is not null)
            return LocalStorageDir;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "docx-mcp");
    }

    /// <summary>
    /// Timeout for connecting to the gRPC server.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Default timeout for gRPC calls.
    /// </summary>
    public TimeSpan DefaultCallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Get effective socket/pipe path for IPC.
    /// The path includes the current process PID to ensure uniqueness
    /// and proper fork/join semantics (each parent gets its own child server).
    /// On Windows, returns a named pipe path. On Unix, returns a socket path.
    /// </summary>
    public string GetEffectiveSocketPath()
    {
        if (UnixSocketPath is not null)
            return UnixSocketPath;

        var pid = Environment.ProcessId;

        if (OperatingSystem.IsWindows())
        {
            // Windows named pipe - unique per process
            return $@"\\.\pipe\docx-storage-local-{pid}";
        }

        // Unix socket - unique per process
        var socketName = $"docx-storage-local-{pid}.sock";
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return runtimeDir is not null
            ? Path.Combine(runtimeDir, socketName)
            : Path.Combine("/tmp", socketName);
    }

    /// <summary>
    /// Check if we're using Windows named pipes.
    /// </summary>
    public bool IsWindowsNamedPipe => OperatingSystem.IsWindows() && UnixSocketPath is null;

    /// <summary>
    /// Create options from environment variables.
    /// </summary>
    public static StorageClientOptions FromEnvironment()
    {
        var options = new StorageClientOptions();

        var serverUrl = Environment.GetEnvironmentVariable("STORAGE_GRPC_URL");
        if (!string.IsNullOrEmpty(serverUrl))
            options.ServerUrl = serverUrl;

        var syncServerUrl = Environment.GetEnvironmentVariable("SYNC_GRPC_URL");
        if (!string.IsNullOrEmpty(syncServerUrl))
            options.SyncServerUrl = syncServerUrl;

        var socketPath = Environment.GetEnvironmentVariable("STORAGE_GRPC_SOCKET");
        if (!string.IsNullOrEmpty(socketPath))
            options.UnixSocketPath = socketPath;

        var serverPath = Environment.GetEnvironmentVariable("STORAGE_SERVER_PATH");
        if (!string.IsNullOrEmpty(serverPath))
            options.StorageServerPath = serverPath;

        var autoLaunch = Environment.GetEnvironmentVariable("STORAGE_AUTO_LAUNCH");
        if (autoLaunch is not null && autoLaunch.Equals("false", StringComparison.OrdinalIgnoreCase))
            options.AutoLaunch = false;

        // Support both new and legacy environment variable names
        var localStorageDir = Environment.GetEnvironmentVariable("LOCAL_STORAGE_DIR")
            ?? Environment.GetEnvironmentVariable("DOCX_SESSIONS_DIR");
        if (!string.IsNullOrEmpty(localStorageDir))
            options.LocalStorageDir = localStorageDir;

        return options;
    }
}
