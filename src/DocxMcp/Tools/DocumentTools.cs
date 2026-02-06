using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;
using DocxMcp.ExternalChanges;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class DocumentTools
{
    [McpServerTool(Name = "document_open"), Description(
        "Open an existing DOCX file or create a new empty document. " +
        "Returns a session ID to use with other tools. " +
        "If path is omitted, creates a new empty document. " +
        "For existing files, external changes will be monitored automatically.")]
    public static string DocumentOpen(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Absolute path to the .docx file to open. Omit to create a new empty document.")]
        string? path = null)
    {
        var session = path is not null
            ? sessions.Open(path)
            : sessions.Create();

        // Start watching for external changes if we have a source file
        if (session.SourcePath is not null && externalChangeTracker is not null)
        {
            externalChangeTracker.StartWatching(session.Id);
        }

        var source = session.SourcePath is not null
            ? $" from '{session.SourcePath}'"
            : " (new document)";

        return $"Opened document{source}. Session ID: {session.Id}";
    }

    [McpServerTool(Name = "document_set_source"), Description(
        "Set or change the file path where a document will be saved. " +
        "Use this for 'Save As' operations or to set a save path for new documents. " +
        "If auto_sync is true (default), the document will be auto-saved after each edit.")]
    public static string DocumentSetSource(
        SessionManager sessions,
        [Description("Session ID of the document.")]
        string doc_id,
        [Description("Absolute path where the document should be saved.")]
        string path,
        [Description("Enable auto-save after each edit. Default true.")]
        bool auto_sync = true)
    {
        sessions.SetSource(doc_id, path, auto_sync);
        return $"Source set to '{path}' for session '{doc_id}'. Auto-sync: {(auto_sync ? "enabled" : "disabled")}.";
    }

    [McpServerTool(Name = "document_save"), Description(
        "Save the document to disk. " +
        "Documents opened from a file are auto-saved after each edit by default (DOCX_AUTO_SAVE=true). " +
        "Use this tool for 'Save As' (providing output_path) or to save new documents that have no source path. " +
        "Updates the external change tracker snapshot after saving.")]
    public static string DocumentSave(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Session ID of the document to save.")]
        string doc_id,
        [Description("Path to save the file to. If omitted, saves to the original path.")]
        string? output_path = null)
    {
        sessions.Save(doc_id, output_path);

        // Update the external change tracker's snapshot after save
        externalChangeTracker?.UpdateSessionSnapshot(doc_id);

        var session = sessions.Get(doc_id);
        var target = output_path ?? session.SourcePath ?? "(unknown)";
        return $"Document saved to '{target}'.";
    }

    [McpServerTool(Name = "document_list"), Description(
        "List all currently open document sessions with track changes status.")]
    public static string DocumentList(SessionManager sessions)
    {
        var list = sessions.List();
        if (list.Count == 0)
            return "No open documents.";

        var arr = new JsonArray();
        foreach (var s in list)
        {
            var session = sessions.Get(s.Id);
            var stats = RevisionHelper.GetRevisionStats(session.Document);

            var obj = new JsonObject
            {
                ["id"] = s.Id,
                ["path"] = s.Path,
                ["track_changes_enabled"] = stats.TrackChangesEnabled,
                ["pending_revisions"] = stats.TotalCount
            };
            arr.Add((JsonNode)obj);
        }

        var result = new JsonObject
        {
            ["count"] = list.Count,
            ["sessions"] = arr
        };

        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Close a document session and release resources.
    /// WARNING: This operation is intentionally NOT exposed as an MCP tool.
    /// Sessions should only be closed via the CLI for administrative purposes.
    /// This will delete all persisted data (baseline, WAL, checkpoints).
    /// </summary>
    public static string DocumentClose(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        string doc_id)
    {
        // Stop watching for external changes before closing
        externalChangeTracker?.StopWatching(doc_id);

        sessions.Close(doc_id);
        return $"Document session '{doc_id}' closed.";
    }

    /// <summary>
    /// Create a snapshot of the document's current state.
    /// This compacts the write-ahead log by writing a new baseline and clearing pending changes.
    /// WARNING: This operation is intentionally NOT exposed as an MCP tool.
    /// WAL compaction should only be performed via the CLI for administrative purposes.
    /// </summary>
    public static string DocumentSnapshot(
        SessionManager sessions,
        [Description("Session ID of the document to snapshot.")]
        string doc_id,
        [Description("If true, discard redo history when compacting. Default false.")]
        bool discard_redo = false)
    {
        sessions.Compact(doc_id, discard_redo);
        return $"Snapshot created for session '{doc_id}'. WAL compacted.";
    }
}
