using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocxMcp.Grpc;
using Grpc.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class ConnectionTools
{
    [McpServerTool(Name = "list_connections"), Description(
        "List available storage connections for the current user. " +
        "ALWAYS call this first to discover which source types are available before using list_connection_files or document_open. " +
        "Returns only the connections actually configured for this deployment.")]
    public static string ListConnections(
        TenantScope tenant,
        SyncManager sync,
        [Description("Filter by source type: 'local', 'google_drive', 'onedrive'. Omit to list all.")]
        string? source_type = null)
    {
        try
        {
            SourceType? filter = source_type switch
            {
                "local" => SourceType.LocalFile,
                "google_drive" => SourceType.GoogleDrive,
                "onedrive" => SourceType.Onedrive,
                _ => null
            };

            var connections = sync.ListConnections(tenant.TenantId, filter);

            var arr = new JsonArray();
            foreach (var c in connections)
            {
                var obj = new JsonObject
                {
                    ["connection_id"] = c.ConnectionId,
                    ["type"] = c.Type.ToString(),
                    ["display_name"] = c.DisplayName
                };
                if (c.ProviderAccountId is not null)
                    obj["provider_account_id"] = c.ProviderAccountId;
                arr.Add((JsonNode)obj);
            }

            var result = new JsonObject
            {
                ["count"] = connections.Count,
                ["connections"] = arr
            };

            return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, "listing connections"); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "list_connection_files"), Description(
        "Browse files and folders in a storage connection. " +
        "Call list_connections first to discover available source types and connection IDs. " +
        "Supports folder navigation and pagination. " +
        "Returns .docx files and all folders (for navigation).")]
    public static string ListConnectionFiles(
        TenantScope tenant,
        SyncManager sync,
        [Description("Source type from list_connections result (e.g. 'local', 'google_drive', 'onedrive').")]
        string source_type,
        [Description("Connection ID from list_connections result. Required for cloud sources.")]
        string? connection_id = null,
        [Description("Folder path to browse. Omit for root.")]
        string? path = null,
        [Description("Pagination token from previous response.")]
        string? page_token = null,
        [Description("Max results per page. Default 20.")]
        int page_size = 20)
    {
        try
        {
            var type = source_type switch
            {
                "local" => SourceType.LocalFile,
                "google_drive" => SourceType.GoogleDrive,
                "onedrive" => SourceType.Onedrive,
                _ => throw new ArgumentException($"Unknown source type: {source_type}. Use 'local', 'google_drive', or 'onedrive'.")
            };

            var result = sync.ListFiles(tenant.TenantId, type, connection_id, path, page_token, page_size);

            var filesArr = new JsonArray();
            foreach (var f in result.Files)
            {
                var obj = new JsonObject
                {
                    ["name"] = f.Name,
                    ["is_folder"] = f.IsFolder,
                };
                if (!f.IsFolder)
                {
                    obj["size_bytes"] = f.SizeBytes;
                    if (f.ModifiedAtUnix > 0)
                        obj["modified_at"] = DateTimeOffset.FromUnixTimeSeconds(f.ModifiedAtUnix).ToString("o");
                }
                if (f.FileId is not null)
                    obj["file_id"] = f.FileId;
                obj["path"] = f.Path;
                filesArr.Add((JsonNode)obj);
            }

            var response = new JsonObject
            {
                ["count"] = result.Files.Count,
                ["files"] = filesArr
            };

            if (result.NextPageToken is not null)
                response["next_page_token"] = result.NextPageToken;

            return response.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, "listing files"); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }
}
