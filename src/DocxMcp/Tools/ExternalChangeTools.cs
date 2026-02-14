using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxMcp.ExternalChanges;
using ModelContextProtocol.Server;

namespace DocxMcp.Tools;

/// <summary>
/// MCP tool for handling external document changes.
/// Single unified tool that detects, displays, and acknowledges external modifications.
/// </summary>
[McpServerToolType]
public sealed class ExternalChangeTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Check for external changes, get details, and optionally acknowledge them.
    /// This is the single tool for all external change operations.
    /// </summary>
    [McpServerTool(Name = "get_external_changes"), Description(
        "Check if the source file has been modified externally and get change details.\n\n" +
        "This tool:\n" +
        "1. Detects if the source file was modified outside this session\n" +
        "2. Shows detailed diff (what was added, removed, modified, moved)\n" +
        "3. Can acknowledge changes to allow continued editing\n\n" +
        "IMPORTANT: If external changes are detected, you MUST acknowledge them " +
        "(set acknowledge=true) before you can continue editing this document.")]
    public static string GetExternalChanges(
        ExternalChangeTracker? tracker,
        [Description("Session ID to check for external changes")]
        string doc_id,
        [Description("Set to true to acknowledge the changes and allow editing to continue")]
        bool acknowledge = false)
    {
        if (tracker is null)
            return """{"has_changes": false, "can_edit": true, "message": "External change tracking not available in HTTP mode."}""";

        // First check for any already-detected pending changes
        var pending = tracker.GetLatestUnacknowledgedChange(doc_id);

        // If no pending, check for new changes
        if (pending is null)
        {
            pending = tracker.CheckForChanges(doc_id);
        }

        // No changes detected
        if (pending is null)
        {
            var noChangesResult = new JsonObject
            {
                ["has_changes"] = false,
                ["can_edit"] = true,
                ["message"] = "No external changes detected. The document is in sync with the source file."
            };
            return noChangesResult.ToJsonString(JsonOptions);
        }

        // Acknowledge if requested
        if (acknowledge)
        {
            tracker.AcknowledgeChange(doc_id, pending.Id);

            var ackResult = new JsonObject
            {
                ["has_changes"] = true,
                ["acknowledged"] = true,
                ["can_edit"] = true,
                ["change_id"] = pending.Id,
                ["detected_at"] = pending.DetectedAt.ToString("o"),
                ["source_path"] = pending.SourcePath,
                ["summary"] = BuildSummaryJson(pending.Summary),
                ["changes"] = BuildChangesJson(pending.Changes),
                ["message"] = $"External changes acknowledged. You may now continue editing.\n\n" +
                              $"Summary: {pending.Summary.TotalChanges} change(s) were made externally:\n" +
                              $"  • {pending.Summary.Added} added\n" +
                              $"  • {pending.Summary.Removed} removed\n" +
                              $"  • {pending.Summary.Modified} modified\n" +
                              $"  • {pending.Summary.Moved} moved"
            };
            return ackResult.ToJsonString(JsonOptions);
        }

        // Return details without acknowledging
        var result = new JsonObject
        {
            ["has_changes"] = true,
            ["acknowledged"] = false,
            ["can_edit"] = false,
            ["change_id"] = pending.Id,
            ["detected_at"] = pending.DetectedAt.ToString("o"),
            ["source_path"] = pending.SourcePath,
            ["summary"] = BuildSummaryJson(pending.Summary),
            ["changes"] = BuildChangesJson(pending.Changes),
            ["patches"] = new JsonArray(pending.Patches.Select(p => (JsonNode)p.ToJsonString()).ToArray()),
            ["message"] = BuildChangeMessage(pending)
        };
        return result.ToJsonString(JsonOptions);
    }

    /// <summary>
    /// Synchronize the session with external file changes.
    /// Reloads the document from disk, re-assigns all element IDs, detects uncovered changes,
    /// and records the sync in the WAL for undo/redo support.
    /// </summary>
    [McpServerTool(Name = "sync_external_changes"), Description(
        "Synchronize session with external file changes. This is the recommended way to handle " +
        "external modifications as it:\n\n" +
        "1. Reloads the document from disk\n" +
        "2. Re-assigns all element IDs for consistency\n" +
        "3. Detects uncovered changes (headers, footers, images, styles, etc.)\n" +
        "4. Records the sync in the edit history (supports undo)\n" +
        "5. Optionally acknowledges a pending change\n\n" +
        "Use this tool when you want to accept external changes and continue editing.")]
    public static string SyncExternalChanges(
        ExternalChangeTracker? tracker,
        [Description("Session ID to sync")]
        string doc_id,
        [Description("Optional change ID to acknowledge (from get_external_changes)")]
        string? change_id = null)
    {
        if (tracker is null)
            return """{"success": false, "message": "External change tracking not available in HTTP mode."}""";

        var syncResult = tracker.SyncExternalChanges(doc_id, change_id);

        var result = new JsonObject
        {
            ["success"] = syncResult.Success,
            ["has_changes"] = syncResult.HasChanges,
            ["message"] = syncResult.Message
        };

        if (syncResult.Summary is not null)
        {
            result["summary"] = BuildSummaryJson(syncResult.Summary);
        }

        if (syncResult.UncoveredChanges is { Count: > 0 })
        {
            var uncoveredArr = new JsonArray();
            foreach (var u in syncResult.UncoveredChanges)
            {
                var uObj = new JsonObject
                {
                    ["type"] = u.Type.ToString(),
                    ["description"] = u.Description,
                    ["change_kind"] = u.ChangeKind
                };
                if (u.PartUri is not null)
                {
                    uObj["part_uri"] = u.PartUri;
                }
                uncoveredArr.Add((JsonNode?)uObj);
            }
            result["uncovered_changes"] = uncoveredArr;
        }

        if (syncResult.WalPosition.HasValue)
        {
            result["wal_position"] = syncResult.WalPosition.Value;
        }

        if (syncResult.AcknowledgedChangeId is not null)
        {
            result["acknowledged_change_id"] = syncResult.AcknowledgedChangeId;
        }

        return result.ToJsonString(JsonOptions);
    }

    private static JsonObject BuildSummaryJson(Diff.DiffSummary summary)
    {
        return new JsonObject
        {
            ["total_changes"] = summary.TotalChanges,
            ["added"] = summary.Added,
            ["removed"] = summary.Removed,
            ["modified"] = summary.Modified,
            ["moved"] = summary.Moved
        };
    }

    private static JsonArray BuildChangesJson(IReadOnlyList<ExternalElementChange> changes)
    {
        var arr = new JsonArray();
        foreach (var c in changes)
        {
            var obj = new JsonObject
            {
                ["type"] = c.ChangeType,
                ["element_type"] = c.ElementType,
                ["description"] = c.Description
            };
            if (c.OldText is not null)
            {
                obj["old_text"] = c.OldText;
            }
            if (c.NewText is not null)
            {
                obj["new_text"] = c.NewText;
            }
            arr.Add((JsonNode?)obj);
        }
        return arr;
    }

    private static string BuildChangeMessage(ExternalChangePatch patch)
    {
        var lines = new List<string>
        {
            "EXTERNAL CHANGES DETECTED",
            "",
            $"The file '{Path.GetFileName(patch.SourcePath)}' was modified externally.",
            $"Detected at: {patch.DetectedAt:yyyy-MM-dd HH:mm:ss UTC}",
            "",
            "## Summary",
            $"  • Added: {patch.Summary.Added}",
            $"  • Removed: {patch.Summary.Removed}",
            $"  • Modified: {patch.Summary.Modified}",
            $"  • Moved: {patch.Summary.Moved}",
            $"  • Total: {patch.Summary.TotalChanges}",
            ""
        };

        if (patch.Changes.Count > 0)
        {
            lines.Add("## Changes");
            foreach (var change in patch.Changes.Take(15))
            {
                lines.Add($"  • {change.Description}");
            }
            if (patch.Changes.Count > 15)
            {
                lines.Add($"  • ... and {patch.Changes.Count - 15} more");
            }
            lines.Add("");
        }

        lines.Add("## Action Required");
        lines.Add("Call `get_external_changes` with `acknowledge=true` to continue editing,");
        lines.Add("or use `sync_external_changes` to reload the document and record in history.");

        return string.Join("\n", lines);
    }
}
