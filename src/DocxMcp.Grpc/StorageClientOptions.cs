namespace DocxMcp.Grpc;

/// <summary>
/// Configuration options for the gRPC storage client.
/// </summary>
public sealed class StorageClientOptions
{
    /// <summary>
    /// gRPC server URL (e.g., "http://localhost:50051").
    /// If null, auto-launch mode uses Unix socket.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Path to Unix socket (e.g., "/tmp/docx-mcp-storage.sock").
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
    /// Timeout for connecting to the gRPC server.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Default timeout for gRPC calls.
    /// </summary>
    public TimeSpan DefaultCallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Get effective Unix socket path.
    /// </summary>
    public string GetEffectiveUnixSocketPath()
    {
        if (UnixSocketPath is not null)
            return UnixSocketPath;

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return runtimeDir is not null
            ? Path.Combine(runtimeDir, "docx-mcp-storage.sock")
            : "/tmp/docx-mcp-storage.sock";
    }

    /// <summary>
    /// Create options from environment variables.
    /// </summary>
    public static StorageClientOptions FromEnvironment()
    {
        var options = new StorageClientOptions();

        var serverUrl = Environment.GetEnvironmentVariable("STORAGE_GRPC_URL");
        if (!string.IsNullOrEmpty(serverUrl))
            options.ServerUrl = serverUrl;

        var socketPath = Environment.GetEnvironmentVariable("STORAGE_GRPC_SOCKET");
        if (!string.IsNullOrEmpty(socketPath))
            options.UnixSocketPath = socketPath;

        var serverPath = Environment.GetEnvironmentVariable("STORAGE_SERVER_PATH");
        if (!string.IsNullOrEmpty(serverPath))
            options.StorageServerPath = serverPath;

        var autoLaunch = Environment.GetEnvironmentVariable("STORAGE_AUTO_LAUNCH");
        if (autoLaunch is not null && autoLaunch.Equals("false", StringComparison.OrdinalIgnoreCase))
            options.AutoLaunch = false;

        return options;
    }
}
