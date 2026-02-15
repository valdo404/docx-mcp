using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxMcp.Diff;
using DocxMcp.ExternalChanges;
using DocxMcp.Helpers;
using DocxMcp.Persistence;
using DocumentFormat.OpenXml.Packaging;
using ModelContextProtocol.Server;

namespace DocxMcp.Tools;

/// <summary>
/// MCP tools for detecting and syncing external document changes.
/// Uses ExternalChangeGate for pending state tracking (blocks edits until acknowledged).
/// </summary>
[McpServerToolType]
public sealed class ExternalChangeTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "get_external_changes"), Description(
        "Check if the source file has been modified externally and get change details.\n\n" +
        "Compares the current in-memory session with the source file on disk.\n" +
        "Returns a diff summary showing what was added, removed, modified, or moved.\n\n" +
        "IMPORTANT: If external changes are detected, you MUST acknowledge them " +
        "(set acknowledge=true) before you can continue editing this document.\n\n" +
        "Use sync_external_changes to reload the document from disk if changes are detected.")]
    public static string GetExternalChanges(
        TenantScope tenant,
        ExternalChangeGate gate,
        [Description("Session ID to check for external changes.")]
        string doc_id,
        [Description("Set to true to acknowledge the changes and allow editing to continue.")]
        bool acknowledge = false)
    {
        var pending = gate.CheckForChanges(tenant.TenantId, tenant.Sessions, doc_id);

        // No changes detected
        if (pending is null)
        {
            return new JsonObject
            {
                ["has_changes"] = false,
                ["can_edit"] = true,
                ["message"] = "No external changes detected. The document is in sync with the source file."
            }.ToJsonString(JsonOptions);
        }

        // Acknowledge if requested
        if (acknowledge)
        {
            gate.Acknowledge(tenant.TenantId, doc_id);

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
                              $"  - {pending.Summary.Added} added\n" +
                              $"  - {pending.Summary.Removed} removed\n" +
                              $"  - {pending.Summary.Modified} modified\n" +
                              $"  - {pending.Summary.Moved} moved"
            };
            return ackResult.ToJsonString(JsonOptions);
        }

        // Return details without acknowledging — editing is blocked
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
            ["message"] = BuildChangeMessage(pending)
        };
        return result.ToJsonString(JsonOptions);
    }

    [McpServerTool(Name = "sync_external_changes"), Description(
        "Synchronize session with external file changes. This:\n\n" +
        "1. Reloads the document from disk\n" +
        "2. Re-assigns all element IDs for consistency\n" +
        "3. Detects uncovered changes (headers, footers, images, styles, etc.)\n" +
        "4. Records the sync in the edit history (supports undo)\n\n" +
        "Use this tool when you want to accept external changes and continue editing.\n" +
        "This also clears any pending change gate, allowing edits to resume.")]
    public static string SyncExternalChanges(
        TenantScope tenant,
        SyncManager sync,
        ExternalChangeGate gate,
        [Description("Session ID to sync.")]
        string doc_id)
    {
        var syncResult = PerformSync(tenant.Sessions, doc_id, isImport: false);

        // Clear pending state after sync (whether successful or not for "no changes")
        if (syncResult.Success)
            gate.ClearPending(tenant.TenantId, doc_id);

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
                    uObj["part_uri"] = u.PartUri;
                uncoveredArr.Add((JsonNode?)uObj);
            }
            result["uncovered_changes"] = uncoveredArr;
        }

        if (syncResult.WalPosition.HasValue)
            result["wal_position"] = syncResult.WalPosition.Value;

        // Auto-save after sync
        if (syncResult.Success && syncResult.HasChanges)
        {
            var session = tenant.Sessions.Get(doc_id);
            sync.MaybeAutoSave(tenant.TenantId, doc_id, session.ToBytes());
        }

        return result.ToJsonString(JsonOptions);
    }

    /// <summary>
    /// Core sync logic: reload from disk, diff, re-assign IDs, create WAL entry.
    /// </summary>
    internal static SyncResult PerformSync(SessionManager sessions, string sessionId, bool isImport)
    {
        try
        {
            var session = sessions.Get(sessionId);
            if (session.SourcePath is null)
                return SyncResult.Failure("Session has no source path. Cannot sync.");

            if (!File.Exists(session.SourcePath))
                return SyncResult.Failure($"Source file not found: {session.SourcePath}");

            // 1. Read external file
            var newBytes = File.ReadAllBytes(session.SourcePath);
            var previousBytes = session.ToBytes();

            // 2. Compute content hashes (ignoring IDs) for change detection
            var previousContentHash = ContentHasher.ComputeContentHash(previousBytes);
            var newContentHash = ContentHasher.ComputeContentHash(newBytes);

            if (previousContentHash == newContentHash)
                return SyncResult.NoChanges();

            // 3. Compute full byte hashes for WAL metadata
            var previousHash = ComputeBytesHash(previousBytes);
            var newHash = ComputeBytesHash(newBytes);

            // 4. Open new document and detect changes
            List<UncoveredChange> uncoveredChanges;
            DiffResult diff;

            using (var newStream = new MemoryStream(newBytes))
            using (var newDoc = WordprocessingDocument.Open(newStream, isEditable: false))
            {
                uncoveredChanges = DiffEngine.DetectUncoveredChanges(session.Document, newDoc);
                diff = DiffEngine.Compare(previousBytes, newBytes);
            }

            // 5. Create new session with re-assigned IDs
            var newSession = DocxSession.FromBytes(newBytes, session.Id, session.SourcePath);
            ElementIdManager.EnsureNamespace(newSession.Document);
            ElementIdManager.EnsureAllIds(newSession.Document);

            var finalBytes = newSession.ToBytes();

            // 6. Build WAL entry with full document snapshot
            var walEntry = new WalEntry
            {
                EntryType = isImport ? WalEntryType.Import : WalEntryType.ExternalSync,
                Timestamp = DateTime.UtcNow,
                Patches = JsonSerializer.Serialize(diff.ToPatches(), DocxMcp.Models.DocxJsonContext.Default.ListJsonObject),
                Description = BuildSyncDescription(diff.Summary, uncoveredChanges),
                SyncMeta = new ExternalSyncMeta
                {
                    SourcePath = session.SourcePath,
                    PreviousHash = previousHash,
                    NewHash = newHash,
                    Summary = diff.Summary,
                    UncoveredChanges = uncoveredChanges,
                    DocumentSnapshot = finalBytes
                }
            };

            // 7. Append to WAL + checkpoint + replace session
            var walPosition = sessions.AppendExternalSync(sessionId, walEntry, newSession);

            return SyncResult.Synced(diff.Summary, uncoveredChanges, diff.ToPatches(), null, walPosition);
        }
        catch (Exception ex)
        {
            return SyncResult.Failure($"Sync failed: {ex.Message}");
        }
    }

    private static JsonObject BuildSummaryJson(DiffSummary summary)
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
        foreach (var c in changes.Take(20))
        {
            var obj = new JsonObject
            {
                ["type"] = c.ChangeType,
                ["element_type"] = c.ElementType,
                ["description"] = c.Description
            };
            if (c.OldText is not null)
                obj["old_text"] = c.OldText;
            if (c.NewText is not null)
                obj["new_text"] = c.NewText;
            arr.Add((JsonNode?)obj);
        }
        return arr;
    }

    private static string BuildChangeMessage(PendingExternalChange pending)
    {
        return $"EXTERNAL CHANGES DETECTED\n\n" +
               $"The file '{Path.GetFileName(pending.SourcePath)}' was modified externally.\n" +
               $"Detected at: {pending.DetectedAt:yyyy-MM-dd HH:mm:ss UTC}\n\n" +
               $"Summary: +{pending.Summary.Added} -{pending.Summary.Removed} ~{pending.Summary.Modified}\n\n" +
               "Call get_external_changes with acknowledge=true to continue editing,\n" +
               "or use sync_external_changes to reload the document and record in history.";
    }

    private static string BuildSyncDescription(DiffSummary summary, List<UncoveredChange> uncovered)
    {
        var parts = new List<string> { "[EXTERNAL SYNC]" };

        if (summary.TotalChanges > 0)
            parts.Add($"+{summary.Added} -{summary.Removed} ~{summary.Modified}");
        else
            parts.Add("no body changes");

        if (uncovered.Count > 0)
        {
            var types = uncovered
                .Select(u => u.Type.ToString().ToLowerInvariant())
                .Distinct()
                .Take(3);
            parts.Add($"({uncovered.Count} uncovered: {string.Join(", ", types)})");
        }

        return string.Join(" ", parts);
    }

    private static string ComputeBytesHash(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
