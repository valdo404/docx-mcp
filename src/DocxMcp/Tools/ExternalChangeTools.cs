using System.ComponentModel;
using System.Text.Json;
using DocxMcp.ExternalChanges;
using ModelContextProtocol.Server;

namespace DocxMcp.Tools;

/// <summary>
/// MCP tools for handling external document changes.
/// These tools allow the LLM to detect, review, and acknowledge external modifications.
/// </summary>
[McpServerToolType]
public static class ExternalChangeTools
{
    /// <summary>
    /// Check if the source file has been modified externally since the session was opened.
    /// Returns detailed information about any detected changes.
    ///
    /// IMPORTANT: If external changes are detected, you MUST review them using
    /// `get_external_changes` and acknowledge them with `acknowledge_external_change`
    /// before continuing to edit the document.
    /// </summary>
    [McpServerTool(Name = "check_external_changes"), Description(
        "Check if the source file has been modified externally since the session was opened. " +
        "Returns details about any detected changes. If changes are found, you MUST acknowledge " +
        "them before continuing to edit the document.")]
    public static ExternalChangeCheckResult CheckExternalChanges(
        ExternalChangeTracker tracker,
        [Description("Session ID to check for external changes")]
        string doc_id)
    {
        // First check for any already-detected pending changes
        var pending = tracker.GetLatestUnacknowledgedChange(doc_id);
        if (pending is not null)
        {
            return new ExternalChangeCheckResult
            {
                HasChanges = true,
                ChangeId = pending.Id,
                DetectedAt = pending.DetectedAt,
                Summary = pending.Summary,
                Message = pending.ToLlmSummary(),
                RequiresAcknowledgment = true
            };
        }

        // Check for new changes
        var patch = tracker.CheckForChanges(doc_id);
        if (patch is not null)
        {
            return new ExternalChangeCheckResult
            {
                HasChanges = true,
                ChangeId = patch.Id,
                DetectedAt = patch.DetectedAt,
                Summary = patch.Summary,
                Message = patch.ToLlmSummary(),
                RequiresAcknowledgment = true
            };
        }

        return new ExternalChangeCheckResult
        {
            HasChanges = false,
            Message = "No external changes detected. The document is up to date with the source file."
        };
    }

    /// <summary>
    /// Get detailed information about pending external changes for a session.
    /// This includes the full diff and generated patches.
    /// </summary>
    [McpServerTool(Name = "get_external_changes"), Description(
        "Get detailed information about pending external changes for a session. " +
        "Returns the full diff showing what was added, removed, modified, or moved.")]
    public static ExternalChangeDetailsResult GetExternalChanges(
        ExternalChangeTracker tracker,
        [Description("Session ID to get external changes for")]
        string doc_id,
        [Description("Specific change ID to retrieve (optional, defaults to most recent)")]
        string? change_id = null)
    {
        var pending = tracker.GetPendingChanges(doc_id);

        if (!pending.HasPendingChanges)
        {
            return new ExternalChangeDetailsResult
            {
                Found = false,
                Message = "No pending external changes for this session."
            };
        }

        ExternalChangePatch? patch;
        if (change_id is not null)
        {
            patch = pending.Changes.FirstOrDefault(c => c.Id == change_id);
            if (patch is null)
            {
                return new ExternalChangeDetailsResult
                {
                    Found = false,
                    Message = $"Change with ID '{change_id}' not found."
                };
            }
        }
        else
        {
            patch = pending.MostRecentPending;
            if (patch is null)
            {
                return new ExternalChangeDetailsResult
                {
                    Found = false,
                    Message = "No unacknowledged external changes."
                };
            }
        }

        return new ExternalChangeDetailsResult
        {
            Found = true,
            ChangeId = patch.Id,
            SessionId = patch.SessionId,
            DetectedAt = patch.DetectedAt,
            SourcePath = patch.SourcePath,
            Summary = patch.Summary,
            Changes = patch.Changes,
            Patches = patch.Patches.Select(p => p.ToJsonString()).ToList(),
            Acknowledged = patch.Acknowledged,
            Message = patch.ToLlmSummary()
        };
    }

    /// <summary>
    /// Acknowledge an external change, allowing continued editing of the document.
    /// You must review the changes before acknowledging.
    /// </summary>
    [McpServerTool(Name = "acknowledge_external_change"), Description(
        "Acknowledge an external change after reviewing it. This allows you to continue " +
        "editing the document. You should review the changes first using `get_external_changes`.")]
    public static AcknowledgeResult AcknowledgeExternalChange(
        ExternalChangeTracker tracker,
        [Description("Session ID")]
        string doc_id,
        [Description("Change ID to acknowledge (use 'all' to acknowledge all pending changes)")]
        string change_id)
    {
        if (change_id.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var count = tracker.AcknowledgeAllChanges(doc_id);
            return new AcknowledgeResult
            {
                Success = count > 0,
                Message = count > 0
                    ? $"Acknowledged {count} external change(s). You may now continue editing."
                    : "No pending changes to acknowledge."
            };
        }

        var success = tracker.AcknowledgeChange(doc_id, change_id);
        return new AcknowledgeResult
        {
            Success = success,
            Message = success
                ? $"External change '{change_id}' acknowledged. You may now continue editing."
                : $"Change '{change_id}' not found or already acknowledged."
        };
    }

    /// <summary>
    /// List all sessions with pending external changes.
    /// </summary>
    [McpServerTool(Name = "list_pending_external_changes"), Description(
        "List all sessions that have pending external changes requiring acknowledgment.")]
    public static PendingChangesListResult ListPendingExternalChanges(
        SessionManager sessions,
        ExternalChangeTracker tracker)
    {
        var sessionsWithChanges = new List<SessionPendingChanges>();

        foreach (var (id, path) in sessions.List())
        {
            if (tracker.HasPendingChanges(id))
            {
                var pending = tracker.GetPendingChanges(id);
                var latest = pending.MostRecentPending;
                if (latest is not null)
                {
                    sessionsWithChanges.Add(new SessionPendingChanges
                    {
                        SessionId = id,
                        SourcePath = path,
                        ChangeId = latest.Id,
                        DetectedAt = latest.DetectedAt,
                        TotalChanges = latest.Summary.TotalChanges
                    });
                }
            }
        }

        return new PendingChangesListResult
        {
            SessionsWithPendingChanges = sessionsWithChanges,
            TotalSessions = sessionsWithChanges.Count,
            Message = sessionsWithChanges.Count > 0
                ? $"{sessionsWithChanges.Count} session(s) have pending external changes requiring review."
                : "No sessions have pending external changes."
        };
    }
}

#region Result Types

public sealed class ExternalChangeCheckResult
{
    public required bool HasChanges { get; init; }
    public string? ChangeId { get; init; }
    public DateTime? DetectedAt { get; init; }
    public Diff.DiffSummary? Summary { get; init; }
    public required string Message { get; init; }
    public bool RequiresAcknowledgment { get; init; }
}

public sealed class ExternalChangeDetailsResult
{
    public required bool Found { get; init; }
    public string? ChangeId { get; init; }
    public string? SessionId { get; init; }
    public DateTime? DetectedAt { get; init; }
    public string? SourcePath { get; init; }
    public Diff.DiffSummary? Summary { get; init; }
    public List<ExternalElementChange>? Changes { get; init; }
    public List<string>? Patches { get; init; }
    public bool Acknowledged { get; init; }
    public required string Message { get; init; }
}

public sealed class AcknowledgeResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
}

public sealed class PendingChangesListResult
{
    public required List<SessionPendingChanges> SessionsWithPendingChanges { get; init; }
    public required int TotalSessions { get; init; }
    public required string Message { get; init; }
}

public sealed class SessionPendingChanges
{
    public required string SessionId { get; init; }
    public string? SourcePath { get; init; }
    public required string ChangeId { get; init; }
    public required DateTime DetectedAt { get; init; }
    public required int TotalChanges { get; init; }
}

#endregion
