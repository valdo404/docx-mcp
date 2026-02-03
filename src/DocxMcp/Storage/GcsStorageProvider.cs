using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocxMcp.Storage;

/// <summary>
/// Google Cloud Storage provider for session persistence.
/// Supports workload identity, service account, and ADC authentication.
/// </summary>
public sealed class GcsStorageProvider : IStorageProvider
{
    private readonly StorageClient _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly ILogger<GcsStorageProvider> _logger;

    public string ProviderType => "gcs";

    public GcsStorageProvider(IOptions<StorageOptions> options, ILogger<GcsStorageProvider> logger)
    {
        var opts = options.Value;
        _bucket = opts.GcsBucket;
        _prefix = opts.GcsPrefix.TrimEnd('/') + "/";
        _logger = logger;

        if (string.IsNullOrEmpty(_bucket))
            throw new ArgumentException("GCS bucket name is required");

        // Create client with credentials
        if (!string.IsNullOrEmpty(opts.GcsCredentialsPath) && File.Exists(opts.GcsCredentialsPath))
        {
            var credential = GoogleCredential.FromFile(opts.GcsCredentialsPath);
            _client = StorageClient.Create(credential);
            _logger.LogInformation("GCS client created with service account from file");
        }
        else
        {
            // Use Application Default Credentials (ADC) - workload identity, env var, etc.
            _client = StorageClient.Create();
            _logger.LogInformation("GCS client created with Application Default Credentials");
        }
    }

    public async Task<byte[]> ReadAsync(string path, CancellationToken ct = default)
    {
        var objectName = GetObjectName(path);
        using var ms = new MemoryStream();
        await _client.DownloadObjectAsync(_bucket, objectName, ms, cancellationToken: ct);
        return ms.ToArray();
    }

    public async Task WriteAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var objectName = GetObjectName(path);
        using var ms = new MemoryStream(data);
        await _client.UploadObjectAsync(_bucket, objectName, "application/octet-stream", ms, cancellationToken: ct);
    }

    public async Task WriteAsync(string path, Stream data, CancellationToken ct = default)
    {
        var objectName = GetObjectName(path);
        await _client.UploadObjectAsync(_bucket, objectName, "application/octet-stream", data, cancellationToken: ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var objectName = GetObjectName(path);
        try
        {
            await _client.DeleteObjectAsync(_bucket, objectName, cancellationToken: ct);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Object doesn't exist, that's fine
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var objectName = GetObjectName(path);
        try
        {
            await _client.GetObjectAsync(_bucket, objectName, cancellationToken: ct);
            return true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var fullPrefix = GetObjectName(prefix);
        var objects = new List<string>();

        var request = _client.ListObjectsAsync(_bucket, fullPrefix);
        await foreach (var obj in request.WithCancellation(ct))
        {
            // Return path without the base prefix
            var relativePath = obj.Name.StartsWith(_prefix)
                ? obj.Name[_prefix.Length..]
                : obj.Name;
            objects.Add(relativePath);
        }

        return objects;
    }

    public Task EnsureDirectoryAsync(string path, CancellationToken ct = default)
    {
        // GCS doesn't have real directories, no-op
        return Task.CompletedTask;
    }

    public async Task<IAsyncDisposable> AcquireLockAsync(string key, TimeSpan timeout, CancellationToken ct = default)
    {
        var lockPath = $".locks/{key}.lock";
        var gcsLock = new GcsDistributedLock(_client, _bucket, GetObjectName(lockPath), timeout, _logger);
        await gcsLock.AcquireAsync(ct);
        return gcsLock;
    }

    private string GetObjectName(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains(".."))
            throw new ArgumentException("Path traversal not allowed");
        return _prefix + normalized;
    }
}

/// <summary>
/// GCS-based distributed lock using generation-based optimistic locking.
/// </summary>
internal sealed class GcsDistributedLock : IAsyncDisposable
{
    private readonly StorageClient _client;
    private readonly string _bucket;
    private readonly string _objectName;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly string _lockId;
    private bool _acquired;

    public GcsDistributedLock(StorageClient client, string bucket, string objectName, TimeSpan timeout, ILogger logger)
    {
        _client = client;
        _bucket = bucket;
        _objectName = objectName;
        _timeout = timeout;
        _logger = logger;
        _lockId = Guid.NewGuid().ToString("N");
    }

    public async Task AcquireAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Try to create the lock object (fails if exists)
                var lockContent = Encoding.UTF8.GetBytes($"{_lockId}|{DateTime.UtcNow:O}");
                using var ms = new MemoryStream(lockContent);

                await _client.UploadObjectAsync(
                    _bucket,
                    _objectName,
                    "text/plain",
                    ms,
                    new UploadObjectOptions { IfGenerationMatch = 0 }, // Only if doesn't exist
                    ct);

                _acquired = true;
                _logger.LogDebug("Acquired GCS lock: {ObjectName}", _objectName);
                return;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                // Lock exists, check if it's stale
                try
                {
                    var obj = await _client.GetObjectAsync(_bucket, _objectName, cancellationToken: ct);
                    using var ms = new MemoryStream();
                    await _client.DownloadObjectAsync(_bucket, _objectName, ms, cancellationToken: ct);
                    var content = Encoding.UTF8.GetString(ms.ToArray());
                    var parts = content.Split('|');

                    if (parts.Length >= 2 && DateTime.TryParse(parts[1], out var lockTime))
                    {
                        // Consider locks older than 5 minutes as stale
                        if (DateTime.UtcNow - lockTime > TimeSpan.FromMinutes(5))
                        {
                            _logger.LogWarning("Found stale lock, attempting to take over: {ObjectName}", _objectName);
                            await _client.DeleteObjectAsync(_bucket, _objectName,
                                new DeleteObjectOptions { IfGenerationMatch = obj.Generation },
                                ct);
                            continue; // Retry acquiring
                        }
                    }
                }
                catch
                {
                    // Ignore errors checking stale lock
                }

                await Task.Delay(100, ct);
            }
        }

        throw new TimeoutException($"Failed to acquire GCS lock: {_objectName}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_acquired)
        {
            try
            {
                await _client.DeleteObjectAsync(_bucket, _objectName);
                _logger.LogDebug("Released GCS lock: {ObjectName}", _objectName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release GCS lock: {ObjectName}", _objectName);
            }
        }
    }
}
