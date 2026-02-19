using Grpc.Core;
using ModelContextProtocol;

namespace DocxMcp.Helpers;

/// <summary>
/// Wraps gRPC errors as McpException so the MCP SDK includes the error message
/// in tool responses instead of the generic "An error occurred invoking 'tool_name'."
/// </summary>
public static class GrpcErrorHelper
{
    public static McpException Wrap(RpcException ex, string context)
    {
        var message = ex.StatusCode switch
        {
            StatusCode.Unavailable => $"Storage backend unavailable: {context}. The service may be restarting.",
            StatusCode.DeadlineExceeded => $"Storage operation timed out: {context}.",
            StatusCode.NotFound => $"Not found: {context}.",
            StatusCode.Internal => $"Storage internal error: {context} — {ex.Status.Detail}",
            _ => $"Storage error ({ex.StatusCode}): {context} — {ex.Status.Detail}",
        };
        return new McpException(message, ex);
    }

    public static McpException WrapNotFound(string docId)
    {
        return new McpException($"Document '{docId}' not found. Use document_list to see open sessions.");
    }
}
