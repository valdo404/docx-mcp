using DocxMcp.Persistence;
using DocxMcp.Ui.Models;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Ui.Services;

public sealed class SessionBrowserService
{
    private readonly SessionStore _store;
    private readonly ILogger<SessionBrowserService> _logger;
    private readonly LruCache<(string, int), byte[]> _docxCache = new(capacity: 20);

    public SessionBrowserService(SessionStore store, ILogger<SessionBrowserService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public SessionListItem[] ListSessions()
    {
        var index = _store.LoadIndex();
        return index.Sessions.Select(e => new SessionListItem
        {
            Id = e.Id,
            SourcePath = e.SourcePath,
            CreatedAt = e.CreatedAt,
            LastModifiedAt = e.LastModifiedAt,
            WalCount = e.WalCount,
            CursorPosition = e.CursorPosition
        }).ToArray();
    }

    public SessionDetailDto? GetSessionDetail(string sessionId)
    {
        var index = _store.LoadIndex();
        var entry = index.Sessions.Find(e => e.Id == sessionId);
        if (entry is null) return null;

        return new SessionDetailDto
        {
            Id = entry.Id,
            SourcePath = entry.SourcePath,
            CreatedAt = entry.CreatedAt,
            LastModifiedAt = entry.LastModifiedAt,
            WalCount = entry.WalCount,
            CursorPosition = entry.CursorPosition,
            CheckpointPositions = entry.CheckpointPositions.ToArray()
        };
    }

    public int GetCurrentPosition(string sessionId)
    {
        var index = _store.LoadIndex();
        var entry = index.Sessions.Find(e => e.Id == sessionId);
        return entry?.CursorPosition ?? 0;
    }

    public HistoryEntryDto[] GetHistory(string sessionId, int offset, int limit)
    {
        var entries = _store.ReadWalEntries(sessionId);
        var index = _store.LoadIndex();
        var session = index.Sessions.Find(e => e.Id == sessionId);
        var checkpoints = session?.CheckpointPositions ?? [];

        return entries
            .Select((e, i) => new HistoryEntryDto
            {
                Position = i + 1,
                Timestamp = e.Timestamp,
                Description = e.Description ?? SummarizePatch(e.Patches),
                IsCheckpoint = checkpoints.Contains(i + 1),
                Patches = e.Patches
            })
            .Reverse()
            .Skip(offset)
            .Take(limit)
            .ToArray();
    }

    public byte[] GetDocxBytesAtPosition(string sessionId, int position)
    {
        var key = (sessionId, position);
        if (_docxCache.TryGet(key, out var cached))
            return cached;

        var bytes = RebuildAtPosition(sessionId, position);
        _docxCache.Set(key, bytes);
        return bytes;
    }

    private byte[] RebuildAtPosition(string sessionId, int position)
    {
        var index = _store.LoadIndex();
        var entry = index.Sessions.Find(e => e.Id == sessionId)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        if (position == 0)
        {
            return _store.LoadBaseline(sessionId);
        }

        var checkpoints = entry.CheckpointPositions ?? [];
        var (ckptPos, ckptBytes) = _store.LoadNearestCheckpoint(sessionId, position, checkpoints);

        using var session = DocxSession.FromBytes(ckptBytes, sessionId, entry.SourcePath);

        if (position > ckptPos)
        {
            var patches = _store.ReadWalRange(sessionId, ckptPos, position);
            foreach (var patchJson in patches)
            {
                try
                {
                    SessionManager.ReplayPatch(session, patchJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to replay patch during rebuild for session {SessionId}.", sessionId);
                    break;
                }
            }
        }

        session.Document.Save();
        return session.Stream.ToArray();
    }

    private static string SummarizePatch(string patchesJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(patchesJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var count = doc.RootElement.GetArrayLength();
                if (count > 0)
                {
                    var first = doc.RootElement[0];
                    var op = first.TryGetProperty("op", out var opProp) ? opProp.GetString() : "?";
                    var path = first.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : "?";
                    return count == 1 ? $"{op} {path}" : $"{op} {path} (+{count - 1} more)";
                }
            }
        }
        catch { }
        return "(no description)";
    }
}
