namespace DocxMcp.Storage;

/// <summary>
/// Abstract storage provider interface for session persistence.
/// Implementations support local filesystem, GCS, Azure Blob, etc.
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Read a file as bytes.
    /// </summary>
    Task<byte[]> ReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Write bytes to a file, creating or overwriting.
    /// </summary>
    Task WriteAsync(string path, byte[] data, CancellationToken ct = default);

    /// <summary>
    /// Write a stream to a file, creating or overwriting.
    /// </summary>
    Task WriteAsync(string path, Stream data, CancellationToken ct = default);

    /// <summary>
    /// Delete a file.
    /// </summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Check if a file exists.
    /// </summary>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// List files matching a prefix/pattern.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// Create a directory (no-op for object storage).
    /// </summary>
    Task EnsureDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Acquire a distributed lock for the given key.
    /// Returns a disposable that releases the lock when disposed.
    /// </summary>
    Task<IAsyncDisposable> AcquireLockAsync(string key, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// Provider type identifier.
    /// </summary>
    string ProviderType { get; }
}

/// <summary>
/// Storage provider options.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Provider type: "local", "gcs", "azure".
    /// </summary>
    public string Provider { get; set; } = "local";

    /// <summary>
    /// Base path for local storage.
    /// </summary>
    public string LocalBasePath { get; set; } = "";

    /// <summary>
    /// GCS bucket name.
    /// </summary>
    public string GcsBucket { get; set; } = "";

    /// <summary>
    /// GCS base prefix for objects.
    /// </summary>
    public string GcsPrefix { get; set; } = "docx-mcp/sessions/";

    /// <summary>
    /// Path to GCS credentials JSON file (optional if using workload identity).
    /// </summary>
    public string? GcsCredentialsPath { get; set; }

    /// <summary>
    /// Azure Blob container name.
    /// </summary>
    public string AzureContainer { get; set; } = "";

    /// <summary>
    /// Azure Storage connection string.
    /// </summary>
    public string? AzureConnectionString { get; set; }
}
