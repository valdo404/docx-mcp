using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class RevisionTools
{
    [McpServerTool(Name = "revision_list"), Description(
        "List all revisions (tracked changes) in a document.\n\n" +
        "Returns insertions, deletions, moves, and formatting changes with metadata.\n" +
        "Supports filtering by author and type, with pagination.\n\n" +
        "Revision types: insertion, deletion, move_from, move_to, format_change, " +
        "paragraph_insertion, section_change, table_change, row_change, cell_change")]
    public static string RevisionList(
        TenantScope tenant,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Filter by author name (case-insensitive).")] string? author = null,
        [Description("Filter by revision type.")] string? type = null,
        [Description("Number of revisions to skip. Default: 0.")] int? offset = null,
        [Description("Maximum number of revisions to return (1-100). Default: 50.")] int? limit = null)
    {
        var session = tenant.Sessions.Get(doc_id);
        var doc = session.Document;

        var stats = RevisionHelper.GetRevisionStats(doc);
        var revisions = RevisionHelper.ListRevisions(doc, author, type);
        var total = revisions.Count;

        var effectiveOffset = Math.Max(0, offset ?? 0);
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 100);

        var page = revisions
            .Skip(effectiveOffset)
            .Take(effectiveLimit)
            .ToList();

        var arr = new JsonArray();
        foreach (var r in page)
        {
            var obj = new JsonObject
            {
                ["id"] = r.Id,
                ["type"] = r.Type,
                ["author"] = r.Author,
                ["date"] = r.Date?.ToString("o"),
                ["content"] = r.Content
            };

            if (r.ElementId is not null)
                obj["element_id"] = r.ElementId;

            arr.Add((JsonNode)obj);
        }

        var result = new JsonObject
        {
            ["track_changes_enabled"] = stats.TrackChangesEnabled,
            ["total"] = total,
            ["offset"] = effectiveOffset,
            ["limit"] = effectiveLimit,
            ["count"] = page.Count,
            ["revisions"] = arr
        };

        return result.ToJsonString(JsonOpts);
    }

    [McpServerTool(Name = "revision_accept"), Description(
        "Accept a revision by ID.\n\n" +
        "Accepting a revision makes it permanent:\n" +
        "- Insertions: text becomes normal content\n" +
        "- Deletions: text is permanently removed\n" +
        "- Format changes: new formatting is kept\n" +
        "- Moves: content stays at new location")]
    public static string RevisionAccept(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Revision ID to accept.")] int revision_id)
    {
        var session = tenant.Sessions.Get(doc_id);
        var doc = session.Document;

        if (!RevisionHelper.AcceptRevision(doc, revision_id))
            return $"Error: Revision {revision_id} not found.";

        // Append to WAL
        var walObj = new JsonObject
        {
            ["op"] = "accept_revision",
            ["revision_id"] = revision_id
        };
        var walEntry = new JsonArray { (JsonNode)walObj };
        tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString());
        sync.MaybeAutoSave(tenant.TenantId, doc_id, session.ToBytes());

        return $"Accepted revision {revision_id}.";
    }

    [McpServerTool(Name = "revision_reject"), Description(
        "Reject a revision by ID.\n\n" +
        "Rejecting a revision reverts the change:\n" +
        "- Insertions: text is removed\n" +
        "- Deletions: text is restored\n" +
        "- Format changes: previous formatting is restored\n" +
        "- Moves: content returns to original location")]
    public static string RevisionReject(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Revision ID to reject.")] int revision_id)
    {
        var session = tenant.Sessions.Get(doc_id);
        var doc = session.Document;

        if (!RevisionHelper.RejectRevision(doc, revision_id))
            return $"Error: Revision {revision_id} not found.";

        // Append to WAL
        var walObj = new JsonObject
        {
            ["op"] = "reject_revision",
            ["revision_id"] = revision_id
        };
        var walEntry = new JsonArray { (JsonNode)walObj };
        tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString());
        sync.MaybeAutoSave(tenant.TenantId, doc_id, session.ToBytes());

        return $"Rejected revision {revision_id}.";
    }

    [McpServerTool(Name = "track_changes_enable"), Description(
        "Enable or disable Track Changes mode in a document.\n\n" +
        "When enabled, subsequent edits made in Word will be tracked.\n" +
        "Note: Edits made through this MCP server are not automatically tracked.")]
    public static string TrackChangesEnable(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("True to enable, false to disable Track Changes.")] bool enabled)
    {
        var session = tenant.Sessions.Get(doc_id);
        var doc = session.Document;

        RevisionHelper.SetTrackChangesEnabled(doc, enabled);

        // Append to WAL
        var walObj = new JsonObject
        {
            ["op"] = "track_changes_enable",
            ["enabled"] = enabled
        };
        var walEntry = new JsonArray { (JsonNode)walObj };
        tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString());
        sync.MaybeAutoSave(tenant.TenantId, doc_id, session.ToBytes());

        return enabled
            ? "Track Changes enabled. Edits made in Word will be tracked."
            : "Track Changes disabled.";
    }

    // --- WAL Replay Methods ---

    /// <summary>
    /// Replay an accept_revision WAL operation.
    /// </summary>
    internal static void ReplayAcceptRevision(JsonElement patch, WordprocessingDocument doc)
    {
        if (patch.TryGetProperty("revision_id", out var idElem))
        {
            var id = idElem.GetInt32();
            RevisionHelper.AcceptRevision(doc, id);
        }
    }

    /// <summary>
    /// Replay a reject_revision WAL operation.
    /// </summary>
    internal static void ReplayRejectRevision(JsonElement patch, WordprocessingDocument doc)
    {
        if (patch.TryGetProperty("revision_id", out var idElem))
        {
            var id = idElem.GetInt32();
            RevisionHelper.RejectRevision(doc, id);
        }
    }

    /// <summary>
    /// Replay a track_changes_enable WAL operation.
    /// </summary>
    internal static void ReplayTrackChangesEnable(JsonElement patch, WordprocessingDocument doc)
    {
        if (patch.TryGetProperty("enabled", out var enabledElem))
        {
            var enabled = enabledElem.GetBoolean();
            RevisionHelper.SetTrackChangesEnabled(doc, enabled);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };
}
