using System.ComponentModel;
using Grpc.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class HistoryTools
{
    [McpServerTool(Name = "document_undo"), Description(
        "Undo N steps in the document's edit history. " +
        "Rebuilds the document from the nearest checkpoint. " +
        "The undone operations remain in history and can be redone.")]
    public static string DocumentUndo(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Number of steps to undo (default 1).")] int steps = 1)
    {
        try
        {
            var sessions = tenant.Sessions;
            var result = sessions.Undo(doc_id, steps);
            if (result.CurrentBytes is not null)
                sync.MaybeAutoSave(tenant.TenantId, doc_id, result.CurrentBytes);
            return $"{result.Message}\nPosition: {result.Position}, Steps: {result.Steps}";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"undoing changes for '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "document_redo"), Description(
        "Redo N steps in the document's edit history. " +
        "Replays patches forward from the current position. " +
        "Only available after undo — new edits after undo discard redo history.")]
    public static string DocumentRedo(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Number of steps to redo (default 1).")] int steps = 1)
    {
        try
        {
            var sessions = tenant.Sessions;
            var result = sessions.Redo(doc_id, steps);
            if (result.CurrentBytes is not null)
                sync.MaybeAutoSave(tenant.TenantId, doc_id, result.CurrentBytes);
            return $"{result.Message}\nPosition: {result.Position}, Steps: {result.Steps}";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"redoing changes for '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "document_history"), Description(
        "List the edit history for a document. " +
        "Shows WAL entries with timestamps, descriptions, and the current position marker. " +
        "Position 0 is the baseline (original document). " +
        "Supports pagination with offset and limit.")]
    public static string DocumentHistory(
        TenantScope tenant,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Start offset for pagination (default 0).")] int offset = 0,
        [Description("Maximum number of entries to return (default 20).")] int limit = 20)
    {
        try
        {
            limit = Math.Clamp(limit, 1, 100);
            var result = tenant.Sessions.GetHistory(doc_id, offset, limit);

            var lines = new List<string>
            {
                $"History for document '{doc_id}':",
                $"  Total entries: {result.TotalEntries}, Cursor: {result.CursorPosition}",
                $"  Can undo: {result.CanUndo}, Can redo: {result.CanRedo}",
                ""
            };

            foreach (var entry in result.Entries)
            {
                var marker = entry.IsCurrent ? " <-- current" : "";
                var ckpt = entry.IsCheckpoint ? " [checkpoint]" : "";
                var ts = entry.Timestamp != default ? entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss UTC") : "—";

                if (entry.IsExternalSync && entry.SyncSummary is not null)
                {
                    var sync = entry.SyncSummary;
                    var uncoveredInfo = sync.UncoveredCount > 0
                        ? $" ({sync.UncoveredCount} uncovered: {string.Join(", ", sync.UncoveredTypes.Take(3))})"
                        : "";
                    lines.Add($"  [{entry.Position}] {ts} | [EXTERNAL SYNC] +{sync.Added} -{sync.Removed} ~{sync.Modified}{uncoveredInfo}{ckpt}{marker}");
                }
                else
                {
                    lines.Add($"  [{entry.Position}] {ts} | {entry.Description}{ckpt}{marker}");
                }
            }

            return string.Join("\n", lines);
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"loading history for '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "document_jump_to"), Description(
        "Jump to an arbitrary position in the document's edit history. " +
        "Rebuilds the document from the nearest checkpoint. " +
        "Position 0 is the baseline, position N is after N patches applied.")]
    public static string DocumentJumpTo(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("WAL position to jump to (0 = baseline).")] int position)
    {
        try
        {
            var sessions = tenant.Sessions;
            var result = sessions.JumpTo(doc_id, position);
            if (result.CurrentBytes is not null)
                sync.MaybeAutoSave(tenant.TenantId, doc_id, result.CurrentBytes);
            return $"{result.Message}\nPosition: {result.Position}, Steps: {result.Steps}";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"jumping to position for '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }
}
