using System.Security.Cryptography;
using System.Threading.Channels;
using DocxMcp.Ui.Models;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Ui.Services;

public sealed class EventBroadcaster : IDisposable
{
    private readonly string _sessionsDir;
    private readonly ILogger<EventBroadcaster> _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private readonly List<ChannelWriter<SessionEvent>> _subscribers = [];
    private readonly Lock _lock = new();
    private string _lastIndexHash = "";

    public EventBroadcaster(string sessionsDir, ILogger<EventBroadcaster> logger)
    {
        _sessionsDir = sessionsDir;
        _logger = logger;
    }

    public void Start()
    {
        // Compute initial hash
        _lastIndexHash = ComputeIndexHash();

        if (Directory.Exists(_sessionsDir))
        {
            try
            {
                _watcher = new FileSystemWatcher(_sessionsDir, "index.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Changed += (_, _) => CheckForChanges();
                _watcher.EnableRaisingEvents = true;
                _logger.LogInformation("FileSystemWatcher started on {Dir}", _sessionsDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FileSystemWatcher failed; relying on polling only.");
            }
        }

        // Polling fallback every 2s
        _pollTimer = new Timer(_ => CheckForChanges(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Subscribe(ChannelWriter<SessionEvent> writer)
    {
        lock (_lock) _subscribers.Add(writer);
    }

    public void Unsubscribe(ChannelWriter<SessionEvent> writer)
    {
        lock (_lock) _subscribers.Remove(writer);
    }

    private void CheckForChanges()
    {
        try
        {
            var hash = ComputeIndexHash();
            if (hash == _lastIndexHash) return;
            _lastIndexHash = hash;

            Emit(new SessionEvent
            {
                Type = "index.changed",
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking for index changes.");
        }
    }

    private void Emit(SessionEvent evt)
    {
        lock (_lock)
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].TryWrite(evt))
                {
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    private string ComputeIndexHash()
    {
        var path = Path.Combine(_sessionsDir, "index.json");
        if (!File.Exists(path)) return "";

        try
        {
            var bytes = File.ReadAllBytes(path);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "";
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _pollTimer?.Dispose();
    }
}
