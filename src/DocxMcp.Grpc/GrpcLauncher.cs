using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Grpc;

/// <summary>
/// Handles auto-launching the gRPC storage server for local mode.
/// </summary>
public sealed class GrpcLauncher : IDisposable
{
    private readonly StorageClientOptions _options;
    private readonly ILogger<GrpcLauncher>? _logger;
    private Process? _serverProcess;
    private bool _disposed;

    public GrpcLauncher(StorageClientOptions options, ILogger<GrpcLauncher>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Ensure the gRPC server is running.
    /// Returns the connection string to use (Unix socket path or TCP URL).
    /// </summary>
    public async Task<string> EnsureServerRunningAsync(CancellationToken cancellationToken = default)
    {
        // If a server URL is configured, use it directly (no auto-launch)
        if (!string.IsNullOrEmpty(_options.ServerUrl))
        {
            _logger?.LogDebug("Using configured server URL: {Url}", _options.ServerUrl);
            return _options.ServerUrl;
        }

        var socketPath = _options.GetEffectiveUnixSocketPath();

        // Check if server is already running
        if (await IsServerRunningAsync(socketPath, cancellationToken))
        {
            _logger?.LogDebug("Storage server already running at {SocketPath}", socketPath);
            return $"unix://{socketPath}";
        }

        if (!_options.AutoLaunch)
        {
            throw new InvalidOperationException(
                $"Storage server not running at {socketPath} and auto-launch is disabled. " +
                "Set STORAGE_GRPC_URL or start the server manually.");
        }

        // Auto-launch the server
        await LaunchServerAsync(socketPath, cancellationToken);

        return $"unix://{socketPath}";
    }

    private async Task<bool> IsServerRunningAsync(string socketPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(socketPath))
            return false;

        try
        {
            // Try to connect to the socket
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(socketPath);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            await socket.ConnectAsync(endpoint, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            // Server not responding, socket file might be stale
            _logger?.LogDebug("Socket exists but server not responding: {Error}", ex.Message);
            return false;
        }
    }

    private async Task LaunchServerAsync(string socketPath, CancellationToken cancellationToken)
    {
        var serverPath = FindServerBinary();
        if (serverPath is null)
        {
            throw new FileNotFoundException(
                "Could not find docx-mcp-storage binary. " +
                "Set STORAGE_SERVER_PATH or ensure it's in PATH.");
        }

        _logger?.LogInformation("Launching storage server: {Path}", serverPath);

        // Remove stale socket file
        if (File.Exists(socketPath))
        {
            try { File.Delete(socketPath); }
            catch { /* ignore */ }
        }

        // Ensure parent directory exists
        var socketDir = Path.GetDirectoryName(socketPath);
        if (socketDir is not null && !Directory.Exists(socketDir))
        {
            Directory.CreateDirectory(socketDir);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = $"--transport unix --unix-socket \"{socketPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _serverProcess = new Process { StartInfo = startInfo };
        _serverProcess.Start();

        // Wait for server to be ready
        var maxWait = _options.ConnectTimeout;
        var pollInterval = TimeSpan.FromMilliseconds(100);
        var elapsed = TimeSpan.Zero;

        while (elapsed < maxWait)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_serverProcess.HasExited)
            {
                var stderr = await _serverProcess.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Storage server exited unexpectedly with code {_serverProcess.ExitCode}: {stderr}");
            }

            if (await IsServerRunningAsync(socketPath, cancellationToken))
            {
                _logger?.LogInformation("Storage server started successfully");
                return;
            }

            await Task.Delay(pollInterval, cancellationToken);
            elapsed += pollInterval;
        }

        // Timeout
        _serverProcess.Kill();
        throw new TimeoutException(
            $"Storage server did not become ready within {maxWait.TotalSeconds} seconds.");
    }

    private string? FindServerBinary()
    {
        // Check configured path first
        if (!string.IsNullOrEmpty(_options.StorageServerPath))
        {
            if (File.Exists(_options.StorageServerPath))
                return _options.StorageServerPath;
            _logger?.LogWarning("Configured server path not found: {Path}", _options.StorageServerPath);
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            var separator = OperatingSystem.IsWindows() ? ';' : ':';
            var binaryName = OperatingSystem.IsWindows() ? "docx-mcp-storage.exe" : "docx-mcp-storage";

            foreach (var dir in pathEnv.Split(separator))
            {
                var candidate = Path.Combine(dir, binaryName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Check relative to app base directory
        var assemblyDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(assemblyDir))
        {
            var binaryName = OperatingSystem.IsWindows() ? "docx-mcp-storage.exe" : "docx-mcp-storage";

            // For tests and apps running from bin/Debug/net10.0/ or similar
            // Path structure: project/tests/DocxMcp.Tests/bin/Debug/net10.0/
            // Rust binary: project/crates/docx-mcp-storage/target/debug/docx-mcp-storage
            // Also try from project/src/*/bin/Debug/net10.0/
            var relativePaths = new[]
            {
                // Same directory (for deployed apps)
                Path.Combine(assemblyDir, binaryName),
                // From tests/DocxMcp.Tests/bin/Debug/net10.0/ -> crates/docx-mcp-storage/target/
                Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "crates", "docx-mcp-storage", "target", "debug", binaryName),
                Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "crates", "docx-mcp-storage", "target", "release", binaryName),
                // From src/*/bin/Debug/net10.0/ -> crates/docx-mcp-storage/target/
                Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "crates", "docx-mcp-storage", "target", "debug", binaryName),
                // From project root (if running from there)
                Path.Combine(assemblyDir, "crates", "docx-mcp-storage", "target", "debug", binaryName),
                Path.Combine(assemblyDir, "crates", "docx-mcp-storage", "target", "release", binaryName),
                // Workspace target directory (cargo builds to workspace root by default)
                Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "target", "debug", binaryName),
                Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "target", "release", binaryName),
            };

            foreach (var path in relativePaths)
            {
                var fullPath = Path.GetFullPath(path);
                _logger?.LogDebug("Checking for server binary at: {Path}", fullPath);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_serverProcess is { HasExited: false })
        {
            try
            {
                _logger?.LogInformation("Shutting down storage server");
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error shutting down storage server");
            }
        }

        _serverProcess?.Dispose();
    }
}
