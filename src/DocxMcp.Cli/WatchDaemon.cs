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

        // Stop the tracker's internal FSW to avoid dual-watcher race condition.
        // The daemon will drive change detection via CheckForChanges.
        _tracker.StopWatching(sessionId);
        _tracker.EnsureTracked(sessionId);

        _watchers[$"{sessionId}:{fullPath}"] = watcher;
        _onOutput($"[WATCH] Watching {fileName} for session {sessionId}");

        // Initial sync — diff + import before watching
        _onOutput($"[INIT] Running initial sync for {fileName}...");
        try
        {
            ProcessChange(sessionId, fullPath, isImport: true);
        }
        catch (Exception ex)
        {
            _onOutput($"[WARN] Initial sync failed: {ex.Message}");
        }
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

        // Register existing files and run initial sync
        foreach (var file in Directory.EnumerateFiles(fullPath, pattern,
            includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            var registeredSessionId = TryRegisterExistingFile(file);
            if (registeredSessionId is not null)
            {
                _onOutput($"[INIT] Running initial sync for {Path.GetFileName(file)}...");
                try
                {
                    ProcessChange(registeredSessionId, file, isImport: true);
                }
                catch (Exception ex)
                {
                    _onOutput($"[WARN] Initial sync failed for {Path.GetFileName(file)}: {ex.Message}");
                }
            }
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

    private static bool DebugEnabled =>
        Environment.GetEnvironmentVariable("DEBUG") is not null;

    private void OnFileChanged(string sessionId, string filePath)
    {
        if (DebugEnabled)
            _onOutput($"[DEBUG:watch] FSW fired for {Path.GetFileName(filePath)} (session {sessionId})");

        // Debounce
        var key = $"{sessionId}:{filePath}";
        var now = DateTime.UtcNow;
        if (_debounceTimestamps.TryGetValue(key, out var last) &&
            (now - last).TotalMilliseconds < _debounceMs)
        {
            if (DebugEnabled)
                _onOutput($"[DEBUG:watch] Debounced (last: {(now - last).TotalMilliseconds:F0}ms ago, threshold: {_debounceMs}ms)");
            return;
        }
        _debounceTimestamps[key] = now;

        if (DebugEnabled)
            _onOutput($"[DEBUG:watch] Scheduling ProcessChange after {_debounceMs}ms debounce");

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
                if (DebugEnabled)
                    _onOutput($"[DEBUG:watch] Exception: {ex}");
            }
        });
    }

    private void ProcessChange(string sessionId, string filePath, bool isImport = false)
    {
        if (DebugEnabled)
            _onOutput($"[DEBUG:watch] ProcessChange called for {Path.GetFileName(filePath)}");

        // Always sync into WAL — watch is meant to keep the session in sync automatically
        var result = _tracker.SyncExternalChanges(sessionId, isImport: isImport);
        if (DebugEnabled)
            _onOutput($"[DEBUG:watch] SyncResult: HasChanges={result.HasChanges}, Message={result.Message}");

        if (result.HasChanges)
        {
            _onOutput($"[SYNC] {Path.GetFileName(filePath)} (+{result.Summary!.Added} -{result.Summary.Removed} ~{result.Summary.Modified}) WAL:{result.WalPosition}");

            if (result.Patches is { Count: > 0 })
            {
                var patchesArr = new System.Text.Json.Nodes.JsonArray(
                    result.Patches.Select(p => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonNode.Parse(p.ToJsonString())).ToArray());
                _onOutput(patchesArr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            if (result.UncoveredChanges is { Count: > 0 })
            {
                _onOutput($"  Uncovered: {string.Join(", ", result.UncoveredChanges.Select(u => $"[{u.ChangeKind}] {u.Type}"))}");
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

    private string? TryRegisterExistingFile(string filePath)
    {
        var sessionId = FindSessionForFile(filePath);
        if (sessionId is not null)
        {
            _tracker.StartWatching(sessionId);
            _onOutput($"[TRACK] {Path.GetFileName(filePath)} -> session {sessionId}");
        }
        return sessionId;
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
