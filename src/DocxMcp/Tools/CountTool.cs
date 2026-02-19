using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Grpc.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;
using DocxMcp.Paths;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class CountTool
{
    [McpServerTool(Name = "count_elements"), Description(
        "Count elements matching a typed path without returning their content. " +
        "Use this before querying with [*] to know the total number of elements " +
        "and plan pagination.\n\n" +
        "Examples:\n" +
        "  /body/paragraph[*] — count all paragraphs\n" +
        "  /body/table[*] — count all tables\n" +
        "  /body/heading[*] — count all headings\n" +
        "  /body/table[0]/row[*] — count rows in first table\n" +
        "  /body/paragraph[text~='hello'] — count paragraphs containing 'hello'")]
    public static string CountElements(
        TenantScope tenant,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Typed path with selector (e.g. /body/paragraph[*], /body/table[0]/row[*]).")] string path)
    {
        try
        {
            var session = tenant.Sessions.Get(doc_id);
            var doc = session.Document;

            // Handle special paths with counts
            if (path is "/body" or "body" or "/")
            {
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body is null)
                    throw new InvalidOperationException("Document has no body.");

                var result = new JsonObject
                {
                    ["paragraphs"] = body.Elements<Paragraph>().Count(),
                    ["tables"] = body.Elements<Table>().Count(),
                    ["headings"] = body.Elements<Paragraph>().Count(p => p.IsHeading()),
                    ["total_children"] = body.ChildElements.Count,
                };

                return result.ToJsonString(JsonOpts);
            }

            var parsed = DocxPath.Parse(path);
            var elements = PathResolver.Resolve(parsed, doc);

            var countResult = new JsonObject
            {
                ["path"] = path,
                ["count"] = elements.Count,
            };

            return countResult.ToJsonString(JsonOpts);
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"counting elements in '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}
