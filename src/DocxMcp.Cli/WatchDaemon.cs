using System.Collections.Concurrent;
using DocxMcp;
using DocxMcp.ExternalChanges;

namespace DocxMcp.Cli;

/// <summary>
/// File/folder watch daemon for continuous monitoring of external document changes.
/// Can run in notification-only or auto-sync mode.
/// </summary>
public sealed class WatchDaemon : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly ExternalChangeTracker _tracker;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _debounceTimestamps = new();
    private readonly int _debounceMs;
    private readonly bool _autoSync;
    private readonly Action<string> _onOutput;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public WatchDaemon(
        SessionManager sessions,
        ExternalChangeTracker tracker,
        int debounceMs = 500,
        bool autoSync = false,
        Action<string>? onOutput = null)
    {
        _sessions = sessions;
        _tracker = tracker;
        _debounceMs = debounceMs;
        _autoSync = autoSync;
        _onOutput = onOutput ?? Console.WriteLine;
    }

    /// <summary>
    /// Watch a single file for changes.
    /// </summary>
    /// <param name="sessionId">Session ID associated with the file.</param>
    /// <param name="filePath">Path to the file to watch.</param>
    public void WatchFile(string sessionId, string filePath)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WatchDaemon));

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            _onOutput($"[WARN] File not found: {fullPath}");
            return;
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        var fileName = Path.GetFileName(fullPath);

        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        watcher.Changed += (_, e) => OnFileChanged(sessionId, e.FullPath);
        watcher.Renamed += (_, e) => OnFileRenamed(sessionId, e.OldFullPath, e.FullPath);
        watcher.Deleted += (_, e) => OnFileDeleted(sessionId, e.FullPath);

        _watchers[$"{sessionId}:{fullPath}"] = watcher;
        _onOutput($"[WATCH] Watching {fileName} for session {sessionId}");
    }

    /// <summary>
    /// Watch a folder for .docx file changes.
    /// Creates sessions for files that don't have one.
    /// </summary>
    /// <param name="folderPath">Path to the folder to watch.</param>
    /// <param name="pattern">File pattern to match (default: *.docx).</param>
    /// <param name="includeSubdirectories">Whether to watch subdirectories.</param>
    public void WatchFolder(string folderPath, string pattern = "*.docx", bool includeSubdirectories = false)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WatchDaemon));

        var fullPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullPath))
        {
            _onOutput($"[WARN] Directory not found: {fullPath}");
            return;
        }

        var watcher = new FileSystemWatcher(fullPath, pattern)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = includeSubdirectories,
            EnableRaisingEvents = true
        };

        watcher.Changed += (_, e) => OnFolderFileChanged(e.FullPath);
        watcher.Created += (_, e) => OnFolderFileCreated(e.FullPath);
        watcher.Renamed += (_, e) => OnFolderFileRenamed(e.OldFullPath, e.FullPath);
        watcher.Deleted += (_, e) => OnFolderFileDeleted(e.FullPath);

        _watchers[$"folder:{fullPath}"] = watcher;
        _onOutput($"[WATCH] Watching folder {fullPath} for {pattern}");

        // Register existing files
        foreach (var file in Directory.EnumerateFiles(fullPath, pattern,
            includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            TryRegisterExistingFile(file);
        }
    }

    /// <summary>
    /// Run the daemon until cancellation is requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        _onOutput($"[DAEMON] Started. Mode: {(_autoSync ? "auto-sync" : "notify-only")}. Press Ctrl+C to stop.");

        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
        catch (OperationCanceledException)
        {
            _onOutput("[DAEMON] Stopping...");
        }
    }

    /// <summary>
    /// Stop the daemon.
    /// </summary>
    public void Stop()
    {
        _cts.Cancel();
    }

    private void OnFileChanged(string sessionId, string filePath)
    {
        // Debounce
        var key = $"{sessionId}:{filePath}";
        var now = DateTime.UtcNow;
        if (_debounceTimestamps.TryGetValue(key, out var last) &&
            (now - last).TotalMilliseconds < _debounceMs)
        {
            return;
        }
        _debounceTimestamps[key] = now;

        // Wait for debounce period
        Task.Delay(_debounceMs).ContinueWith(_ =>
        {
            try
            {
                ProcessChange(sessionId, filePath);
            }
            catch (Exception ex)
            {
                _onOutput($"[ERROR] {sessionId}: {ex.Message}");
            }
        });
    }

    private void ProcessChange(string sessionId, string filePath)
    {
        if (_autoSync)
        {
            var result = _tracker.SyncExternalChanges(sessionId);
            if (result.HasChanges)
            {
                _onOutput($"[SYNC] {Path.GetFileName(filePath)}: {result.Message}");
            }
        }
        else
        {
            var patch = _tracker.CheckForChanges(sessionId);
            if (patch is not null)
            {
                _onOutput($"[CHANGE] {Path.GetFileName(filePath)}: " +
                    $"+{patch.Summary.Added} -{patch.Summary.Removed} ~{patch.Summary.Modified}");
                _onOutput($"         Change ID: {patch.Id}");
                _onOutput($"         Use 'sync-external {sessionId}' to sync.");
            }
        }
    }

    private void OnFileRenamed(string sessionId, string oldPath, string newPath)
    {
        _onOutput($"[RENAME] {sessionId}: {Path.GetFileName(oldPath)} -> {Path.GetFileName(newPath)}");
    }

    private void OnFileDeleted(string sessionId, string filePath)
    {
        _onOutput($"[DELETE] {sessionId}: {Path.GetFileName(filePath)} - source file deleted!");
    }

    private void OnFolderFileChanged(string filePath)
    {
        var sessionId = FindSessionForFile(filePath);
        if (sessionId is not null)
        {
            OnFileChanged(sessionId, filePath);
        }
    }

    private void OnFolderFileCreated(string filePath)
    {
        _onOutput($"[NEW] {Path.GetFileName(filePath)} created. Use 'open {filePath}' to start a session.");
    }

    private void OnFolderFileRenamed(string oldPath, string newPath)
    {
        _onOutput($"[RENAME] {Path.GetFileName(oldPath)} -> {Path.GetFileName(newPath)}");
    }

    private void OnFolderFileDeleted(string filePath)
    {
        var sessionId = FindSessionForFile(filePath);
        if (sessionId is not null)
        {
            _onOutput($"[DELETE] {Path.GetFileName(filePath)} deleted (session {sessionId} orphaned)");
        }
        else
        {
            _onOutput($"[DELETE] {Path.GetFileName(filePath)} deleted");
        }
    }

    private string? FindSessionForFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        foreach (var (id, path) in _sessions.List())
        {
            if (path is not null && Path.GetFullPath(path) == fullPath)
            {
                return id;
            }
        }
        return null;
    }

    private void TryRegisterExistingFile(string filePath)
    {
        var sessionId = FindSessionForFile(filePath);
        if (sessionId is not null)
        {
            _tracker.StartWatching(sessionId);
            _onOutput($"[TRACK] {Path.GetFileName(filePath)} -> session {sessionId}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
