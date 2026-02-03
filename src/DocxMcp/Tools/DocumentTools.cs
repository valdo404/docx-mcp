using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class DocumentTools
{
    [McpServerTool(Name = "document_open"), Description(
        "Open an existing DOCX file or create a new empty document. " +
        "Returns a session ID to use with other tools. " +
        "If path is omitted, creates a new empty document.")]
    public static string DocumentOpen(
        SessionManager sessions,
        [Description("Absolute path to the .docx file to open. Omit to create a new empty document.")]
        string? path = null)
    {
        var session = path is not null
            ? sessions.Open(path)
            : sessions.Create();

        var source = session.SourcePath is not null
            ? $" from '{session.SourcePath}'"
            : " (new document)";

        return $"Opened document{source}. Session ID: {session.Id}";
    }

    [McpServerTool(Name = "document_save"), Description(
        "Save the document to disk. " +
        "If output_path is provided, saves to that path (Save As). " +
        "Otherwise saves to the original path.")]
    public static string DocumentSave(
        SessionManager sessions,
        [Description("Session ID of the document to save.")]
        string doc_id,
        [Description("Path to save the file to. If omitted, saves to the original path.")]
        string? output_path = null)
    {
        sessions.Save(doc_id, output_path);
        var session = sessions.Get(doc_id);
        var target = output_path ?? session.SourcePath ?? "(unknown)";
        return $"Document saved to '{target}'.";
    }

    [McpServerTool(Name = "document_list"), Description(
        "List all currently open document sessions.")]
    public static string DocumentList(SessionManager sessions)
    {
        var list = sessions.List();
        if (list.Count == 0)
            return "No open documents.";

        var lines = list.Select(s =>
            $"  {s.Id}: {s.Path ?? "(new document)"}");
        return $"Open documents:\n{string.Join('\n', lines)}";
    }

    /// <summary>
    /// Close a document session and release resources.
    /// WARNING: This operation is intentionally NOT exposed as an MCP tool.
    /// Sessions should only be closed via the CLI for administrative purposes.
    /// This will delete all persisted data (baseline, WAL, checkpoints).
    /// </summary>
    public static string DocumentClose(
        SessionManager sessions,
        string doc_id)
    {
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
