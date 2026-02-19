using System.ComponentModel;
using DocxMcp.Diff;
using Grpc.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class DiffTool
{
    [McpServerTool(Name = "document_diff"), Description(
        "Compare two versions of a document by WAL position.\n\n" +
        "Returns a structured diff showing added, removed, modified, and moved elements.\n" +
        "Position 0 = original baseline. Use document_history to see available positions.\n" +
        "Omit position_b to compare against current state.")]
    public static string DocumentDiff(
        TenantScope tenant,
        [Description("Session ID of the document.")] string doc_id,
        [Description("First WAL position to compare from. 0 = baseline.")] int position_a = 0,
        [Description("Second WAL position to compare to. Omit for current state.")] int? position_b = null)
    {
        try
        {
            byte[] bytesA = tenant.Sessions.GetBytesAtPosition(doc_id, position_a);
            byte[] bytesB;

            if (position_b is not null)
            {
                bytesB = tenant.Sessions.GetBytesAtPosition(doc_id, position_b.Value);
            }
            else
            {
                var session = tenant.Sessions.Get(doc_id);
                bytesB = session.ToBytes();
            }

            var diffResult = DiffEngine.Compare(bytesA, bytesB);
            return diffResult.ToJson();
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"comparing versions of '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }
}
