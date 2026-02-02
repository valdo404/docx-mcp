namespace DocxMcp.Persistence;

/// <summary>
/// IDisposable wrapper around a FileStream opened with FileShare.None,
/// providing cross-process advisory file locking for index mutations.
/// Process crash releases lock automatically (OS closes file descriptors).
/// </summary>
public sealed class SessionLock : IDisposable
{
    private FileStream? _lockStream;

    internal SessionLock(FileStream lockStream) => _lockStream = lockStream;

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _lockStream, null);
        stream?.Dispose();
    }
}
