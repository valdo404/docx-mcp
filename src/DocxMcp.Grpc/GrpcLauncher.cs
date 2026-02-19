using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Grpc;

/// <summary>
/// Handles auto-launching the gRPC storage server for local mode.
/// On Unix: uses Unix domain sockets with PID-based unique paths.
/// On Windows: uses TCP with dynamically allocated ports.
/// </summary>
public sealed class GrpcLauncher : IDisposable
{
    private readonly StorageClientOptions _options;
    private readonly ILogger<GrpcLauncher>? _logger;
    private Process? _serverProcess;
    private string? _launchedSocketPath;
    private int? _launchedPort;
    private bool _disposed;

    public GrpcLauncher(StorageClientOptions options, ILogger<GrpcLauncher>? logger = null)
    {
        _options = options;
        _logger = logger;

        // Ensure child process is killed when parent exits
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        Console.CancelKeyPress += (_, _) => Dispose();
    }

    /// <summary>
    /// Ensure the gRPC server is running.
    /// Returns the connection string to use.
    /// </summary>
    public async Task<string> EnsureServerRunningAsync(CancellationToken cancellationToken = default)
    {
        // If a server URL is configured, use it directly (no auto-launch)
        if (!string.IsNullOrEmpty(_options.ServerUrl))
        {
            _logger?.LogDebug("Using configured server URL: {Url}", _options.ServerUrl);
            return _options.ServerUrl;
        }

        if (OperatingSystem.IsWindows())
        {
            return await EnsureServerRunningTcpAsync(cancellationToken);
        }
        else
        {
            return await EnsureServerRunningUnixAsync(cancellationToken);
        }
    }

    private async Task<string> EnsureServerRunningUnixAsync(CancellationToken cancellationToken)
    {
        var socketPath = _options.GetEffectiveSocketPath();

        // Check if server is already running at this socket
        if (await IsUnixServerRunningAsync(socketPath, cancellationToken))
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
        await LaunchUnixServerAsync(socketPath, cancellationToken);
        _launchedSocketPath = socketPath;

        return $"unix://{socketPath}";
    }

    private async Task<string> EnsureServerRunningTcpAsync(CancellationToken cancellationToken)
    {
        // On Windows, we need to find an available port
        var port = GetAvailablePort();

        if (!_options.AutoLaunch)
        {
            throw new InvalidOperationException(
                "Storage server not running and auto-launch is disabled. " +
                "Set STORAGE_GRPC_URL or start the server manually.");
        }

        // Auto-launch the server on TCP
        await LaunchTcpServerAsync(port, cancellationToken);
        _launchedPort = port;

        return $"http://127.0.0.1:{port}";
    }

    private static int GetAvailablePort()
    {
        // Let the OS assign an available port
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<bool> IsUnixServerRunningAsync(string socketPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(socketPath))
            return false;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(socketPath);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            await socket.ConnectAsync(endpoint, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            _logger?.LogDebug("Socket exists but server not responding: {Error}", ex.Message);
            return false;
        }
    }

    private async Task<bool> IsTcpServerRunningAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            _logger?.LogDebug("TCP port {Port} not responding: {Error}", port, ex.Message);
            return false;
        }
    }

    private async Task LaunchUnixServerAsync(string socketPath, CancellationToken cancellationToken)
    {
        var serverPath = FindServerBinary();
        if (serverPath is null)
        {
            throw new FileNotFoundException(
                "Could not find docx-storage-local binary. " +
                "Set STORAGE_SERVER_PATH or ensure it's in PATH.");
        }

        _logger?.LogInformation("Launching storage server: {Path} (unix socket: {Socket})", serverPath, socketPath);

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

        var parentPid = Environment.ProcessId;
        var logFile = GetLogFilePath();
        var localStorageDir = _options.GetEffectiveLocalStorageDir();

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = $"--transport unix --unix-socket \"{socketPath}\" --local-storage-dir \"{localStorageDir}\" --parent-pid {parentPid}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["RUST_LOG"] = "info";

        _logger?.LogDebug("Storage directory: {Dir}", localStorageDir);

        await LaunchAndWaitAsync(startInfo, () => IsUnixServerRunningAsync(socketPath, cancellationToken), logFile, cancellationToken);
    }

    private async Task LaunchTcpServerAsync(int port, CancellationToken cancellationToken)
    {
        var serverPath = FindServerBinary();
        if (serverPath is null)
        {
            throw new FileNotFoundException(
                "Could not find docx-storage-local binary. " +
                "Set STORAGE_SERVER_PATH or ensure it's in PATH.");
        }

        _logger?.LogInformation("Launching storage server: {Path} (tcp port: {Port})", serverPath, port);

        var parentPid = Environment.ProcessId;
        var logFile = GetLogFilePath();
        var localStorageDir = _options.GetEffectiveLocalStorageDir();

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = $"--transport tcp --port {port} --local-storage-dir \"{localStorageDir}\" --parent-pid {parentPid}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["RUST_LOG"] = "info";

        _logger?.LogDebug("Storage directory: {Dir}", localStorageDir);

        await LaunchAndWaitAsync(startInfo, () => IsTcpServerRunningAsync(port, cancellationToken), logFile, cancellationToken);
    }

    private async Task LaunchAndWaitAsync(ProcessStartInfo startInfo, Func<Task<bool>> isRunning, string logFile, CancellationToken cancellationToken)
    {
        _serverProcess = new Process { StartInfo = startInfo };
        _serverProcess.Start();

        // Redirect output to log file for debugging
        _ = Task.Run(async () =>
        {
            try
            {
                await using var logStream = new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read);
                await using var writer = new StreamWriter(logStream) { AutoFlush = true };

                var stderrTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await _serverProcess.StandardError.ReadLineAsync(cancellationToken)) is not null)
                    {
                        await writer.WriteLineAsync($"[stderr] {line}");
                    }
                }, cancellationToken);

                var stdoutTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await _serverProcess.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
                    {
                        await writer.WriteLineAsync($"[stdout] {line}");
                    }
                }, cancellationToken);

                await Task.WhenAll(stderrTask, stdoutTask);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Expected when process exits
            }
        }, cancellationToken);

        _logger?.LogInformation("Storage server log file: {LogFile}", logFile);

        var maxWait = _options.ConnectTimeout;
        var pollInterval = TimeSpan.FromMilliseconds(100);
        var elapsed = TimeSpan.Zero;

        while (elapsed < maxWait)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_serverProcess.HasExited)
            {
                // Wait a bit for log file to be written
                await Task.Delay(100, cancellationToken);
                var logContent = File.Exists(logFile) ? await File.ReadAllTextAsync(logFile, cancellationToken) : "(no log)";
                throw new InvalidOperationException(
                    $"Storage server exited unexpectedly with code {_serverProcess.ExitCode}. Log:\n{logContent}");
            }

            if (await isRunning())
            {
                _logger?.LogInformation("Storage server started successfully (PID: {Pid})", _serverProcess.Id);
                return;
            }

            await Task.Delay(pollInterval, cancellationToken);
            elapsed += pollInterval;
        }

        _serverProcess.Kill();
        throw new TimeoutException(
            $"Storage server did not become ready within {maxWait.TotalSeconds} seconds.");
    }

    private static string GetLogFilePath()
    {
        var pid = Environment.ProcessId;
        var tempDir = Path.GetTempPath();
        return Path.Combine(tempDir, $"docx-storage-local-{pid}.log");
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

        var binaryName = OperatingSystem.IsWindows() ? "docx-storage-local.exe" : "docx-storage-local";

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            var separator = OperatingSystem.IsWindows() ? ';' : ':';

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
            var platformDir = GetPlatformDir();

            var relativePaths = new[]
            {
                // Same directory (for deployed apps)
                Path.Combine(assemblyDir, binaryName),
                // From tests/DocxMcp.Tests/bin/Debug/net10.0/ -> dist/{platform}/
                Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "dist", platformDir, binaryName),
                // From src/*/bin/Debug/net10.0/ -> dist/{platform}/
                Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "dist", platformDir, binaryName),
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

    private static string GetPlatformDir()
    {
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "macos-arm64" : "macos-x64";
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        if (OperatingSystem.IsWindows())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "windows-arm64" : "windows-x64";
        return "unknown";
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
                _logger?.LogInformation("Shutting down storage server (PID: {Pid})", _serverProcess.Id);
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error shutting down storage server");
            }
        }

        _serverProcess?.Dispose();

        // Clean up socket file (Unix only)
        if (_launchedSocketPath is not null && File.Exists(_launchedSocketPath))
        {
            try
            {
                File.Delete(_launchedSocketPath);
                _logger?.LogDebug("Cleaned up socket file: {SocketPath}", _launchedSocketPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to clean up socket file: {SocketPath}", _launchedSocketPath);
            }
        }
    }
}
