using DocxMcp.Diff;
using DocxMcp.Grpc;
using DocxMcp.Helpers;

namespace DocxMcp.ExternalChanges;

/// <summary>
/// Lightweight gate that tracks pending external changes per session.
/// Blocks edits (via PatchTool) until changes are acknowledged or synced.
///
/// State is persisted in the session index via gRPC (pending_external_change flag),
/// so it survives restarts and is shared across MCP instances.
///
/// Detection sources:
/// - Manual: get_external_changes tool calls CheckForChanges()
/// - Automatic: gRPC WatchChanges stream calls NotifyExternalChange()
/// </summary>
public sealed class ExternalChangeGate
{
    private readonly IHistoryStorage _history;

    public ExternalChangeGate(IHistoryStorage history)
    {
        _history = history;
    }

    /// <summary>
    /// Check if a source file has changed compared to the session.
    /// If changed, sets the pending flag in the index (blocking edits).
    /// Returns change details if changes were detected.
    /// </summary>
    public PendingExternalChange? CheckForChanges(string tenantId, SessionManager sessions, string sessionId)
    {
        // If already pending, compute fresh diff details but don't re-flag
        if (HasPendingChanges(tenantId, sessionId))
        {
            return ComputeChangeDetails(sessions, sessionId);
        }

        var session = sessions.Get(sessionId);
        if (session.SourcePath is null || !File.Exists(session.SourcePath))
            return null;

        var sessionBytes = session.ToBytes();
        var fileBytes = File.ReadAllBytes(session.SourcePath);
        var sessionHash = ContentHasher.ComputeContentHash(sessionBytes);
        var fileHash = ContentHasher.ComputeContentHash(fileBytes);

        if (sessionHash == fileHash)
        {
            // File matches session — ensure flag is cleared
            ClearPending(tenantId, sessionId);
            return null;
        }

        // Content differs — set pending flag and compute diff
        SetPending(tenantId, sessionId, true);

        var diff = DiffEngine.Compare(sessionBytes, fileBytes);

        return new PendingExternalChange
        {
            Id = $"ext_{sessionId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}",
            SessionId = sessionId,
            DetectedAt = DateTime.UtcNow,
            SourcePath = session.SourcePath,
            Summary = diff.Summary,
            Changes = diff.Changes.Select(ExternalElementChange.FromElementChange).ToList()
        };
    }

    /// <summary>
    /// Check if there are pending external changes for a session.
    /// Reads from the gRPC storage index — works across restarts and instances.
    /// </summary>
    public bool HasPendingChanges(string tenantId, string sessionId)
    {
        var (_, pending) = _history.SessionExistsAsync(tenantId, sessionId)
            .GetAwaiter().GetResult();
        return pending;
    }

    /// <summary>
    /// Acknowledge a pending change, allowing edits to continue.
    /// Clears the pending flag in the index.
    /// </summary>
    public bool Acknowledge(string tenantId, string sessionId)
    {
        if (!HasPendingChanges(tenantId, sessionId))
            return false;

        SetPending(tenantId, sessionId, false);
        return true;
    }

    /// <summary>
    /// Clear pending state for a session (after sync or close).
    /// </summary>
    public void ClearPending(string tenantId, string sessionId)
    {
        SetPending(tenantId, sessionId, false);
    }

    /// <summary>
    /// Notify that an external change was detected.
    /// Called by the gRPC WatchChanges stream consumer.
    /// </summary>
    public void NotifyExternalChange(string tenantId, SessionManager sessions, string sessionId)
    {
        CheckForChanges(tenantId, sessions, sessionId);
    }

    /// <summary>
    /// Set the pending_external_change flag in the session index via gRPC.
    /// </summary>
    private void SetPending(string tenantId, string sessionId, bool pending)
    {
        _history.UpdateSessionInIndexAsync(tenantId, sessionId,
            pendingExternalChange: pending).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Compute change details without modifying state.
    /// Used when pending flag is already set to return fresh diff info.
    /// </summary>
    private static PendingExternalChange? ComputeChangeDetails(SessionManager sessions, string sessionId)
    {
        var session = sessions.Get(sessionId);
        if (session.SourcePath is null || !File.Exists(session.SourcePath))
            return null;

        var sessionBytes = session.ToBytes();
        var fileBytes = File.ReadAllBytes(session.SourcePath);
        var diff = DiffEngine.Compare(sessionBytes, fileBytes);

        return new PendingExternalChange
        {
            Id = $"ext_{sessionId}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}",
            SessionId = sessionId,
            DetectedAt = DateTime.UtcNow,
            SourcePath = session.SourcePath,
            Summary = diff.Summary,
            Changes = diff.Changes.Select(ExternalElementChange.FromElementChange).ToList()
        };
    }
}

/// <summary>
/// A pending external change that must be acknowledged before editing.
/// </summary>
public sealed class PendingExternalChange
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required DateTime DetectedAt { get; init; }
    public required string SourcePath { get; init; }
    public required DiffSummary Summary { get; init; }
    public required List<ExternalElementChange> Changes { get; init; }
}
