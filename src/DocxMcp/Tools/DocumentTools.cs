using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;
using DocxMcp.Grpc;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class DocumentTools
{
    [McpServerTool(Name = "document_open"), Description(
        "Open an existing DOCX file or create a new empty document. " +
        "Returns a session ID to use with other tools. " +
        "If all parameters are omitted, creates a new empty document. " +
        "Use list_connections and list_connection_files to discover available files before opening. " +
        "For local files, provide path only. " +
        "For cloud files (Google Drive), provide source_type, connection_id, file_id, and path.")]
    public static string DocumentOpen(
        ILogger<DocumentTools> logger,
        TenantScope tenant,
        SyncManager sync,
        [Description("Absolute path for local files, or display path for cloud files.")]
        string? path = null,
        [Description("Source type: 'local', 'google_drive'. Omit for local or new document.")]
        string? source_type = null,
        [Description("Connection ID from list_connections (required for cloud sources).")]
        string? connection_id = null,
        [Description("Provider file ID from list_connection_files (required for cloud sources).")]
        string? file_id = null)
    {
        try
        {
            logger.LogDebug("document_open: path={Path}, source_type={SourceType}, connection_id={ConnId}, file_id={FileId}",
                path, source_type, connection_id, file_id);

            var sessions = tenant.Sessions;

            DocxSession session;
            string sourceDescription;

            if (path is null && source_type is null && connection_id is null && file_id is null)
            {
                // New empty document — no sync needed, always allowed
                session = sessions.Create();
                sourceDescription = " (new document)";
            }
            else
            {
                // Resolve source type (infer from params, block local in cloud mode)
                var type = ResolveSourceType(source_type, connection_id, sync);

                if (type != SourceType.LocalFile && file_id is not null)
                {
                    // Cloud source: download bytes, create session, register source
                    var data = sync.DownloadFile(tenant.TenantId, type, connection_id, path ?? file_id, file_id);
                    session = sessions.OpenFromBytes(data, path ?? file_id);

                    // Register typed source for sync-back
                    sync.SetSource(tenant.TenantId, session.Id, type, connection_id, path ?? file_id, file_id, autoSync: true);
                    sessions.SetSourcePath(session.Id, path ?? file_id);

                    sourceDescription = $" from {source_type}://{path ?? file_id}";
                }
                else if (path is not null)
                {
                    // Local file
                    session = sessions.Open(path);

                    if (session.SourcePath is not null)
                        sync.RegisterAndWatch(tenant.TenantId, session.Id, session.SourcePath, autoSync: true);

                    sourceDescription = $" from '{session.SourcePath}'";
                }
                else
                {
                    throw new ArgumentException(
                        "Invalid parameters. To open a cloud file, provide source_type + connection_id + file_id. " +
                        "To create a new empty document, omit all parameters.");
                }
            }

            logger.LogDebug("document_open result: session={SessionId}, source={Source}", session.Id, sourceDescription);
            return $"Opened document{sourceDescription}. Session ID: {session.Id}";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, "opening document"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(path ?? "new document"); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "document_set_source"), Description(
        "Set or change where a document will be saved. " +
        "Use this for 'Save As' operations or to set a save target for new documents. " +
        "Use list_connections to discover available storage targets. " +
        "If auto_sync is true (default), the document will be auto-saved after each edit.")]
    public static string DocumentSetSource(
        ILogger<DocumentTools> logger,
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")]
        string doc_id,
        [Description("Path (absolute for local, display path for cloud).")]
        string path,
        [Description("Source type: 'local', 'google_drive'. Default: local.")]
        string? source_type = null,
        [Description("Connection ID from list_connections (required for cloud sources).")]
        string? connection_id = null,
        [Description("Provider file ID (required for cloud sources).")]
        string? file_id = null,
        [Description("Enable auto-save after each edit. Default true.")]
        bool auto_sync = true)
    {
        try
        {
            logger.LogDebug("document_set_source: doc_id={DocId}, path={Path}, source_type={SourceType}, connection_id={ConnId}, file_id={FileId}",
                doc_id, path, source_type, connection_id, file_id);

            var type = ResolveSourceType(source_type, connection_id, sync);

            sync.SetSource(tenant.TenantId, doc_id, type, connection_id, path, file_id, auto_sync);
            tenant.Sessions.SetSourcePath(doc_id, path);

            return $"Source set to '{path}' for session '{doc_id}'. Type: {type}. Auto-sync: {(auto_sync ? "enabled" : "disabled")}.";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, "setting document source"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "document_save"), Description(
        "Save the document to disk. " +
        "Documents opened from a file are auto-saved after each edit by default (DOCX_AUTO_SAVE=true). " +
        "Use this tool for 'Save As' (providing output_path) or to save new documents that have no source path. " +
        "Updates the external change tracker snapshot after saving.")]
    public static string DocumentSave(
        ILogger<DocumentTools> logger,
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document to save.")]
        string doc_id,
        [Description("Path to save the file to. If omitted, saves to the original path.")]
        string? output_path = null)
    {
        try
        {
            logger.LogDebug("document_save: doc_id={DocId}, output_path={OutputPath}", doc_id, output_path);

            var sessions = tenant.Sessions;
            // If output_path is provided, update/register the source first
            if (output_path is not null)
            {
                // Preserve existing source type if registered (don't force LocalFile)
                var existing = sync.GetSyncStatus(tenant.TenantId, doc_id);
                if (existing is not null)
                {
                    logger.LogDebug("document_save: preserving existing source type {SourceType} for session {DocId}",
                        existing.SourceType, doc_id);
                    sync.SetSource(tenant.TenantId, doc_id,
                        existing.SourceType, existing.ConnectionId, output_path,
                        existing.FileId, autoSync: true);
                }
                else
                {
                    sync.SetSource(tenant.TenantId, doc_id, output_path, autoSync: true);
                }
                sessions.SetSourcePath(doc_id, output_path);
            }

            var session = sessions.Get(doc_id);
            sync.Save(tenant.TenantId, doc_id, session.ToBytes());

            var target = output_path ?? session.SourcePath ?? "(unknown)";
            logger.LogDebug("document_save: saved to {Target}", target);
            return $"Document saved to '{target}'.";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, "saving document"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "document_list"), Description(
        "List all currently open document sessions with track changes status.")]
    public static string DocumentList(ILogger<DocumentTools> logger, TenantScope tenant)
    {
        try
        {
            logger.LogDebug("document_list: tenant={TenantId}", tenant.TenantId);
            var sessions = tenant.Sessions;
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
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, "listing documents"); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    /// <summary>
    /// Close a document session and release resources.
    /// WARNING: This operation is intentionally NOT exposed as an MCP tool.
    /// Sessions should only be closed via the CLI for administrative purposes.
    /// This will delete all persisted data (baseline, WAL, checkpoints).
    /// </summary>
    public static string DocumentClose(
        TenantScope tenant,
        SyncManager? sync,
        string doc_id)
    {
        sync?.StopWatch(tenant.TenantId, doc_id);

        tenant.Sessions.Close(doc_id);
        return $"Document session '{doc_id}' closed.";
    }

    /// <summary>
    /// Create a snapshot of the document's current state.
    /// This compacts the write-ahead log by writing a new baseline and clearing pending changes.
    /// WARNING: This operation is intentionally NOT exposed as an MCP tool.
    /// WAL compaction should only be performed via the CLI for administrative purposes.
    /// </summary>
    public static string DocumentSnapshot(
        TenantScope tenant,
        [Description("Session ID of the document to snapshot.")]
        string doc_id,
        [Description("If true, discard redo history when compacting. Default false.")]
        bool discard_redo = false)
    {
        tenant.Sessions.Compact(doc_id, discard_redo);
        return $"Snapshot created for session '{doc_id}'. WAL compacted.";
    }

    /// <summary>
    /// Resolve source_type from explicit param or infer from connection_id.
    /// Blocks local sources in cloud mode (SYNC_GRPC_URL set).
    /// </summary>
    private static SourceType ResolveSourceType(string? source_type, string? connection_id, SyncManager sync)
    {
        if (source_type is not null)
        {
            var resolved = source_type switch
            {
                "google_drive" => SourceType.GoogleDrive,
                "onedrive" => SourceType.Onedrive,
                "local" => SourceType.LocalFile,
                _ => throw new ArgumentException($"Unknown source_type: {source_type}")
            };

            // Block local in cloud mode
            if (resolved == SourceType.LocalFile && sync.IsRemoteSync)
                throw new ArgumentException(
                    "Local file storage is not available in this deployment. Use a cloud source (google_drive, onedrive).");

            return resolved;
        }

        // Infer: connection_id provided → cloud source (google_drive by default)
        if (connection_id is not null)
            return SourceType.GoogleDrive;

        // No connection_id, no source_type → local only if local is available
        if (sync.IsRemoteSync)
            throw new ArgumentException(
                "source_type is required. This deployment uses cloud storage. Call list_connections to discover available sources.");

        return SourceType.LocalFile;
    }
}
