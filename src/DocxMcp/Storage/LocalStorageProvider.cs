using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocxMcp.Storage;

/// <summary>
/// Local filesystem storage provider.
/// Used for development and Docker deployments with mounted volumes.
/// </summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    private readonly string _basePath;
    private readonly ILogger<LocalStorageProvider> _logger;

    public string ProviderType => "local";

    public LocalStorageProvider(IOptions<StorageOptions> options, ILogger<LocalStorageProvider> logger)
    {
        _basePath = !string.IsNullOrEmpty(options.Value.LocalBasePath)
            ? options.Value.LocalBasePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".docx-mcp", "sessions");
        _logger = logger;

        Directory.CreateDirectory(_basePath);
    }

    public Task<byte[]> ReadAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        return File.ReadAllBytesAsync(fullPath, ct);
    }

    public Task WriteAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return File.WriteAllBytesAsync(fullPath, data, ct);
    }

    public async Task WriteAsync(string path, Stream data, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var fs = File.Create(fullPath);
        await data.CopyToAsync(fs, ct);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var fullPrefix = GetFullPath(prefix);
        var dir = Path.GetDirectoryName(fullPrefix) ?? _basePath;
        var pattern = Path.GetFileName(fullPrefix) + "*";

        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var files = Directory.GetFiles(dir, pattern)
            .Select(f => Path.GetRelativePath(_basePath, f).Replace('\\', '/'))
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files);
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken ct = default)
    {
        var fullPath = GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> AcquireLockAsync(string key, TimeSpan timeout, CancellationToken ct = default)
    {
        var lockPath = GetFullPath($".locks/{key}.lock");
        var lockDir = Path.GetDirectoryName(lockPath)!;
        Directory.CreateDirectory(lockDir);

        var lockFile = new LocalFileLock(lockPath, timeout, _logger);
        return Task.FromResult<IAsyncDisposable>(lockFile);
    }

    private string GetFullPath(string relativePath)
    {
        // Normalize and prevent path traversal
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains(".."))
            throw new ArgumentException("Path traversal not allowed");

        return Path.Combine(_basePath, normalized);
    }
}

/// <summary>
/// File-based lock for local storage.
/// </summary>
internal sealed class LocalFileLock : IAsyncDisposable
{
    private readonly string _path;
    private FileStream? _stream;
    private readonly ILogger _logger;

    public LocalFileLock(string path, TimeSpan timeout, ILogger logger)
    {
        _path = path;
        _logger = logger;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }

        throw new TimeoutException($"Failed to acquire lock: {path}");
    }

    public ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            _stream.Dispose();
            try { File.Delete(_path); } catch { }
            _stream = null;
        }
        return ValueTask.CompletedTask;
    }
}
