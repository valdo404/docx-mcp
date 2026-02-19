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
public sealed class CommentTools
{
    [McpServerTool(Name = "comment_add"), Description(
        "Add a comment to a document element.\n\n" +
        "The comment is anchored to the element at the given path. " +
        "If anchor_text is provided, the comment is anchored to that specific text within the element " +
        "(supports cross-run matching). Without anchor_text, the comment spans the entire element.\n\n" +
        "Multi-paragraph comments: use \\n in text for multiple paragraphs.\n\n" +
        "Examples:\n" +
        "  comment_add(doc_id, \"/body/paragraph[0]\", \"Needs revision\")\n" +
        "  comment_add(doc_id, \"/body/paragraph[id='1A2B3C4D']\", \"Fix this phrase\", anchor_text=\"specific words\")")]
    public static string CommentAdd(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Typed path to the target element (must resolve to exactly 1 element).")] string path,
        [Description("Comment text. Use \\n for multi-paragraph comments.")] string text,
        [Description("Optional text within the element to anchor the comment to. Without this, comment spans the entire element.")] string? anchor_text = null,
        [Description("Comment author name. Default: 'AI Assistant'.")] string? author = null,
        [Description("Author initials. Default: 'AI'.")] string? initials = null)
    {
        try
        {
            var session = tenant.Sessions.Get(doc_id);
            var doc = session.Document;

            List<OpenXmlElement> elements;
            try
            {
                var parsed = DocxPath.Parse(path);
                elements = PathResolver.Resolve(parsed, doc);
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }

            if (elements.Count == 0)
                return $"Error: Path '{path}' resolved to 0 elements.";
            if (elements.Count > 1)
                return $"Error: Path '{path}' resolved to {elements.Count} elements — must resolve to exactly 1.";

            var target = elements[0];
            var effectiveAuthor = author ?? "AI Assistant";
            var effectiveInitials = initials ?? "AI";
            var date = DateTime.UtcNow;
            var commentId = CommentHelper.AllocateCommentId(doc);

            try
            {
                if (anchor_text is not null)
                {
                    CommentHelper.AddCommentToText(doc, target, commentId, text,
                        effectiveAuthor, effectiveInitials, date, anchor_text);
                }
                else
                {
                    CommentHelper.AddCommentToElement(doc, target, commentId, text,
                        effectiveAuthor, effectiveInitials, date);
                }
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }

            // Append to WAL
            var walObj = new JsonObject
            {
                ["op"] = "add_comment",
                ["comment_id"] = commentId,
                ["path"] = path,
                ["text"] = text,
                ["author"] = effectiveAuthor,
                ["initials"] = effectiveInitials,
                ["date"] = date.ToString("o"),
                ["anchor_text"] = anchor_text is not null ? JsonValue.Create(anchor_text) : null
            };
            var walEntry = new JsonArray();
            walEntry.Add((JsonNode)walObj);
            var bytes = session.ToBytes();
            tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString(), null, bytes);
            sync.MaybeAutoSave(tenant.TenantId, doc_id, bytes);

            return $"Comment {commentId} added by '{effectiveAuthor}' on {path}.";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"adding comment to '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "comment_list"), Description(
        "List comments in a document with optional filtering and pagination.\n\n" +
        "Returns a JSON object with pagination envelope and array of comment objects " +
        "containing id, author, initials, date, text, and anchored_text.")]
    public static string CommentList(
        TenantScope tenant,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Filter by author name (case-insensitive).")] string? author = null,
        [Description("Number of comments to skip. Default: 0.")] int? offset = null,
        [Description("Maximum number of comments to return (1-50). Default: 50.")] int? limit = null)
    {
        try
        {
            var session = tenant.Sessions.Get(doc_id);
            var doc = session.Document;

            var comments = CommentHelper.ListComments(doc, author);
            var total = comments.Count;

            var effectiveOffset = Math.Max(0, offset ?? 0);
            var effectiveLimit = Math.Clamp(limit ?? 50, 1, 50);

            var page = comments
                .Skip(effectiveOffset)
                .Take(effectiveLimit)
                .ToList();

            var arr = new JsonArray();
            foreach (var c in page)
            {
                var obj = new JsonObject
                {
                    ["id"] = c.Id,
                    ["author"] = c.Author,
                    ["initials"] = c.Initials,
                    ["date"] = c.Date?.ToString("o"),
                    ["text"] = c.Text,
                };

                if (c.AnchoredText is not null)
                    obj["anchored_text"] = c.AnchoredText;

                arr.Add((JsonNode)obj);
            }

            var result = new JsonObject
            {
                ["total"] = total,
                ["offset"] = effectiveOffset,
                ["limit"] = effectiveLimit,
                ["count"] = page.Count,
                ["comments"] = arr
            };

            return result.ToJsonString(JsonOpts);
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"listing comments in '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "comment_delete"), Description(
        "Delete comments from a document by ID or by author.\n\n" +
        "At least one of comment_id or author must be provided.\n" +
        "When deleting by author, each comment generates its own WAL entry for deterministic replay.")]
    public static string CommentDelete(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("ID of the specific comment to delete.")] int? comment_id = null,
        [Description("Delete all comments by this author (case-insensitive).")] string? author = null)
    {
        try
        {
            if (comment_id is null && author is null)
                return "Error: At least one of comment_id or author must be provided.";

            var session = tenant.Sessions.Get(doc_id);
            var doc = session.Document;

            if (comment_id is not null)
            {
                var deleted = CommentHelper.DeleteComment(doc, comment_id.Value);
                if (!deleted)
                    return $"Error: Comment {comment_id.Value} not found.";

                // Append to WAL
                var walObj = new JsonObject
                {
                    ["op"] = "delete_comment",
                    ["comment_id"] = comment_id.Value
                };
                var walEntry = new JsonArray();
                walEntry.Add((JsonNode)walObj);
                var bytes = session.ToBytes();
                tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString(), null, bytes);
                sync.MaybeAutoSave(tenant.TenantId, doc_id, bytes);

                return "Deleted 1 comment(s).";
            }

            // Delete by author — expand to individual WAL entries
            var comments = CommentHelper.ListComments(doc, author);
            if (comments.Count == 0)
                return $"Error: No comments found by author '{author}'.";

            var deletedCount = 0;
            byte[]? lastBytes = null;
            foreach (var c in comments)
            {
                if (CommentHelper.DeleteComment(doc, c.Id))
                {
                    var walObj = new JsonObject
                    {
                        ["op"] = "delete_comment",
                        ["comment_id"] = c.Id
                    };
                    var walEntry = new JsonArray();
                    walEntry.Add((JsonNode)walObj);
                    lastBytes = session.ToBytes();
                    tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString(), null, lastBytes);
                    deletedCount++;
                }
            }

            // Auto-save after all deletions
            if (deletedCount > 0 && lastBytes is not null)
                sync.MaybeAutoSave(tenant.TenantId, doc_id, lastBytes);

            return $"Deleted {deletedCount} comment(s).";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"deleting comment in '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    /// <summary>
    /// Replay an add_comment WAL operation.
    /// </summary>
    internal static void ReplayAddComment(JsonElement patch, WordprocessingDocument doc)
    {
        var commentId = patch.GetProperty("comment_id").GetInt32();
        var pathStr = patch.GetProperty("path").GetString()
            ?? throw new InvalidOperationException("add_comment must have a 'path' field.");
        var text = patch.GetProperty("text").GetString() ?? "";
        var author = patch.GetProperty("author").GetString() ?? "AI Assistant";
        var initials = patch.GetProperty("initials").GetString() ?? "AI";
        var dateStr = patch.GetProperty("date").GetString();
        var date = dateStr is not null ? DateTime.Parse(dateStr).ToUniversalTime() : DateTime.UtcNow;

        string? anchorText = null;
        if (patch.TryGetProperty("anchor_text", out var at) && at.ValueKind == JsonValueKind.String)
            anchorText = at.GetString();

        var parsed = DocxPath.Parse(pathStr);
        var elements = PathResolver.Resolve(parsed, doc);
        if (elements.Count != 1)
            throw new InvalidOperationException($"add_comment path must resolve to exactly 1 element, got {elements.Count}.");

        var target = elements[0];

        if (anchorText is not null)
        {
            CommentHelper.AddCommentToText(doc, target, commentId, text, author, initials, date, anchorText);
        }
        else
        {
            CommentHelper.AddCommentToElement(doc, target, commentId, text, author, initials, date);
        }
    }

    /// <summary>
    /// Replay a delete_comment WAL operation.
    /// </summary>
    internal static void ReplayDeleteComment(JsonElement patch, WordprocessingDocument doc)
    {
        var commentId = patch.GetProperty("comment_id").GetInt32();
        CommentHelper.DeleteComment(doc, commentId);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}
