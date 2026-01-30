using System.Collections.Concurrent;

namespace DocxMcp;

/// <summary>
/// Thread-safe manager for document sessions.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, DocxSession> _sessions = new();

    public DocxSession Open(string path)
    {
        var session = DocxSession.Open(path);
        if (!_sessions.TryAdd(session.Id, session))
        {
            session.Dispose();
            throw new InvalidOperationException("Session ID collision — this should not happen.");
        }
        return session;
    }

    public DocxSession Create()
    {
        var session = DocxSession.Create();
        if (!_sessions.TryAdd(session.Id, session))
        {
            session.Dispose();
            throw new InvalidOperationException("Session ID collision — this should not happen.");
        }
        return session;
    }

    public DocxSession Get(string id)
    {
        if (_sessions.TryGetValue(id, out var session))
            return session;
        throw new KeyNotFoundException($"No document session with ID '{id}'.");
    }

    public void Save(string id, string? path = null)
    {
        var session = Get(id);
        session.Save(path);
    }

    public void Close(string id)
    {
        if (_sessions.TryRemove(id, out var session))
            session.Dispose();
        else
            throw new KeyNotFoundException($"No document session with ID '{id}'.");
    }

    public IReadOnlyList<(string Id, string? Path)> List()
    {
        return _sessions.Values
            .Select(s => (s.Id, s.SourcePath))
            .ToList()
            .AsReadOnly();
    }
}
