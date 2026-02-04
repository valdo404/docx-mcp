using System.Text;
using DocxMcp.WordAddin.Models;

namespace DocxMcp.WordAddin.Services;

/// <summary>
/// Service to compute logical patches from user changes detected via Office.js.
/// These are semantic changes (added, removed, modified, moved) that help the LLM
/// understand what the user is doing.
/// </summary>
public sealed class UserChangeService
{
    private readonly ILogger<UserChangeService> _logger;

    // Store recent changes per session for context
    private readonly Dictionary<string, List<LogicalChange>> _sessionChanges = new();
    private readonly Lock _lock = new();

    public UserChangeService(ILogger<UserChangeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Process a user change report and compute logical changes.
    /// </summary>
    public UserChangeResult ProcessChanges(UserChangeReport report)
    {
        var changes = ComputeLogicalChanges(report.Before, report.After);

        // Store for future context
        lock (_lock)
        {
            if (!_sessionChanges.TryGetValue(report.SessionId, out var sessionList))
            {
                sessionList = [];
                _sessionChanges[report.SessionId] = sessionList;
            }

            sessionList.AddRange(changes);

            // Keep only last 50 changes per session
            if (sessionList.Count > 50)
            {
                sessionList.RemoveRange(0, sessionList.Count - 50);
            }
        }

        _logger.LogInformation(
            "Processed {Count} logical changes for session {SessionId}",
            changes.Count, report.SessionId);

        return new UserChangeResult
        {
            Changes = changes,
            Summary = BuildSummary(changes)
        };
    }

    /// <summary>
    /// Get recent changes for a session (for LLM context).
    /// </summary>
    public List<LogicalChange> GetRecentChanges(string sessionId, int limit = 10)
    {
        lock (_lock)
        {
            if (_sessionChanges.TryGetValue(sessionId, out var changes))
            {
                return changes.TakeLast(limit).Reverse().ToList();
            }
            return [];
        }
    }

    /// <summary>
    /// Clear change history for a session.
    /// </summary>
    public void ClearSession(string sessionId)
    {
        lock (_lock)
        {
            _sessionChanges.Remove(sessionId);
        }
    }

    /// <summary>
    /// Compute logical changes between two document states.
    /// Uses content-based matching (similar to DiffEngine in docx-mcp).
    /// </summary>
    private List<LogicalChange> ComputeLogicalChanges(DocumentContent before, DocumentContent after)
    {
        var changes = new List<LogicalChange>();
        var timestamp = DateTime.UtcNow;

        var beforeElements = before.Elements ?? [];
        var afterElements = after.Elements ?? [];

        // Build lookup by ID
        var beforeById = beforeElements.ToDictionary(e => e.Id);
        var afterById = afterElements.ToDictionary(e => e.Id);

        // Find removed elements
        foreach (var elem in beforeElements)
        {
            if (!afterById.ContainsKey(elem.Id))
            {
                changes.Add(new LogicalChange
                {
                    ChangeType = "removed",
                    ElementType = elem.Type,
                    Description = $"Removed {elem.Type}: \"{Truncate(elem.Text, 50)}\"",
                    OldText = elem.Text,
                    Timestamp = timestamp
                });
            }
        }

        // Find added elements
        foreach (var elem in afterElements)
        {
            if (!beforeById.ContainsKey(elem.Id))
            {
                changes.Add(new LogicalChange
                {
                    ChangeType = "added",
                    ElementType = elem.Type,
                    Description = $"Added {elem.Type}: \"{Truncate(elem.Text, 50)}\"",
                    NewText = elem.Text,
                    Timestamp = timestamp
                });
            }
        }

        // Find modified and moved elements
        foreach (var afterElem in afterElements)
        {
            if (beforeById.TryGetValue(afterElem.Id, out var beforeElem))
            {
                // Check for content modification
                if (beforeElem.Text != afterElem.Text)
                {
                    changes.Add(new LogicalChange
                    {
                        ChangeType = "modified",
                        ElementType = afterElem.Type,
                        Description = BuildModificationDescription(beforeElem.Text, afterElem.Text),
                        OldText = beforeElem.Text,
                        NewText = afterElem.Text,
                        Timestamp = timestamp
                    });
                }

                // Check for move (position change)
                if (beforeElem.Index != afterElem.Index && beforeElem.Text == afterElem.Text)
                {
                    changes.Add(new LogicalChange
                    {
                        ChangeType = "moved",
                        ElementType = afterElem.Type,
                        Description = $"Moved {afterElem.Type} from position {beforeElem.Index} to {afterElem.Index}",
                        OldText = afterElem.Text,
                        NewText = afterElem.Text,
                        Timestamp = timestamp
                    });
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Build a human-readable description of a text modification.
    /// </summary>
    private static string BuildModificationDescription(string oldText, string newText)
    {
        // Simple heuristics for common operations
        if (string.IsNullOrWhiteSpace(oldText) && !string.IsNullOrWhiteSpace(newText))
        {
            return $"Added text: \"{Truncate(newText, 50)}\"";
        }

        if (!string.IsNullOrWhiteSpace(oldText) && string.IsNullOrWhiteSpace(newText))
        {
            return $"Cleared text (was: \"{Truncate(oldText, 50)}\")";
        }

        // Check for extension (appended text)
        if (newText.StartsWith(oldText))
        {
            var added = newText[oldText.Length..].Trim();
            return $"Extended text, added: \"{Truncate(added, 40)}\"";
        }

        // Check for prefix (prepended text)
        if (newText.EndsWith(oldText))
        {
            var added = newText[..^oldText.Length].Trim();
            return $"Prepended text: \"{Truncate(added, 40)}\"";
        }

        // General modification
        var lengthDiff = newText.Length - oldText.Length;
        var direction = lengthDiff > 0 ? "expanded" : lengthDiff < 0 ? "shortened" : "modified";

        return $"Text {direction}: \"{Truncate(oldText, 25)}\" → \"{Truncate(newText, 25)}\"";
    }

    private static string BuildSummary(List<LogicalChange> changes)
    {
        if (changes.Count == 0)
            return "No changes detected.";

        var added = changes.Count(c => c.ChangeType == "added");
        var removed = changes.Count(c => c.ChangeType == "removed");
        var modified = changes.Count(c => c.ChangeType == "modified");
        var moved = changes.Count(c => c.ChangeType == "moved");

        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} added");
        if (removed > 0) parts.Add($"{removed} removed");
        if (modified > 0) parts.Add($"{modified} modified");
        if (moved > 0) parts.Add($"{moved} moved");

        return $"{changes.Count} change(s): {string.Join(", ", parts)}";
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}
