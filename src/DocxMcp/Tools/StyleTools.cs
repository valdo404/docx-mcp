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
public sealed class StyleTools
{
    [McpServerTool(Name = "style_element"), Description(
        "Apply character/run-level formatting using merge semantics — only specified properties change, all others are preserved.\n\n" +
        "Properties (set value to apply, true/false for toggles, JSON null to remove):\n" +
        "  bold, italic, underline, strike — true sets, false removes\n" +
        "  font_size — integer in points (e.g. 14)\n" +
        "  font_name — string (e.g. \"Arial\")\n" +
        "  color — hex string (e.g. \"FF0000\")\n" +
        "  highlight — color name (yellow, green, cyan, etc.)\n" +
        "  vertical_align — superscript, subscript, baseline\n\n" +
        "Omit path to style ALL runs in the document (including inside tables).\n" +
        "With path, styles all runs within the resolved element(s).\n" +
        "Use [id='...'] for stable targeting (e.g. /body/paragraph[id='1A2B3C4D']/run[id='5E6F7A8B']).\n" +
        "Use [*] wildcards for batch operations (e.g. /body/paragraph[*]).")]
    public static string StyleElement(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("JSON object of run-level style properties to merge.")] string style,
        [Description("Optional typed path. Omit to style all runs in the document.")] string? path = null)
    {
        try
        {
            var session = tenant.Sessions.Get(doc_id);
            var doc = session.Document;
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new InvalidOperationException("Document has no body.");

            JsonElement styleEl;
            try
            {
                styleEl = JsonDocument.Parse(style).RootElement;
            }
            catch (JsonException ex)
            {
                return $"Error: Invalid style JSON — {ex.Message}";
            }

            if (styleEl.ValueKind != JsonValueKind.Object)
                return "Error: style must be a JSON object.";

            List<Run> runs;
            if (path is null)
            {
                runs = body.Descendants<Run>().ToList();
            }
            else
            {
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

                runs = new List<Run>();
                foreach (var el in elements)
                {
                    runs.AddRange(StyleHelper.CollectRuns(el));
                }
            }

            if (runs.Count == 0)
                return "No runs found to style.";

            var trackChanges = RevisionHelper.IsTrackChangesEnabled(doc);

            foreach (var run in runs)
            {
                if (trackChanges)
                {
                    // Create RunProperties from style JSON and apply with tracking
                    var newProps = ElementFactory.CreateRunProperties(styleEl);
                    RevisionHelper.ApplyRunPropertiesWithTracking(doc, run, newProps);
                }
                else
                {
                    StyleHelper.MergeRunProperties(run, styleEl);
                }
            }

            // Append to WAL
            var walObj = new JsonObject
            {
                ["op"] = "style_element",
                ["path"] = path,
                ["style"] = JsonNode.Parse(style)
            };
            var walEntry = new JsonArray { (JsonNode)walObj };
            var bytes = session.ToBytes();
            tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString(), null, bytes);
            sync.MaybeAutoSave(tenant.TenantId, doc_id, bytes);

            return $"Styled {runs.Count} run(s).";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"styling element in '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "style_paragraph"), Description(
        "Apply paragraph-level formatting using merge semantics — only specified properties change, all others are preserved.\n\n" +
        "Properties (set value to apply, JSON null to remove):\n" +
        "  alignment — left, center, right, justify\n" +
        "  style — paragraph style name (e.g. \"Heading1\")\n" +
        "  spacing_before, spacing_after — integer in twips\n" +
        "  line_spacing — integer in twips\n" +
        "  indent_left, indent_right — integer in twips\n" +
        "  indent_first_line, indent_hanging — integer in twips\n" +
        "  shading — hex color string for background (e.g. \"FFFF00\")\n\n" +
        "Compound properties (spacing, indentation) merge sub-fields independently.\n" +
        "Omit path to style ALL paragraphs in the document (including inside tables).\n" +
        "Use [id='...'] for stable targeting (e.g. /body/paragraph[id='1A2B3C4D']).\n" +
        "Use [*] wildcards for batch operations.")]
    public static string StyleParagraph(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("JSON object of paragraph-level style properties to merge.")] string style,
        [Description("Optional typed path. Omit to style all paragraphs in the document.")] string? path = null)
    {
        try
        {
            var session = tenant.Sessions.Get(doc_id);
            var doc = session.Document;
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new InvalidOperationException("Document has no body.");

            JsonElement styleEl;
            try
            {
                styleEl = JsonDocument.Parse(style).RootElement;
            }
            catch (JsonException ex)
            {
                return $"Error: Invalid style JSON — {ex.Message}";
            }

            if (styleEl.ValueKind != JsonValueKind.Object)
                return "Error: style must be a JSON object.";

            List<Paragraph> paragraphs;
            if (path is null)
            {
                paragraphs = body.Descendants<Paragraph>().ToList();
            }
            else
            {
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

                paragraphs = new List<Paragraph>();
                foreach (var el in elements)
                {
                    paragraphs.AddRange(StyleHelper.CollectParagraphs(el));
                }
            }

            if (paragraphs.Count == 0)
                return "No paragraphs found to style.";

            var trackChanges = RevisionHelper.IsTrackChangesEnabled(doc);

            foreach (var para in paragraphs)
            {
                if (trackChanges)
                {
                    // Create ParagraphProperties from style JSON and apply with tracking
                    var newProps = ElementFactory.CreateParagraphProperties(styleEl);
                    RevisionHelper.ApplyParagraphPropertiesWithTracking(doc, para, newProps);
                }
                else
                {
                    StyleHelper.MergeParagraphProperties(para, styleEl);
                }
            }

            // Append to WAL
            var walObj = new JsonObject
            {
                ["op"] = "style_paragraph",
                ["path"] = path,
                ["style"] = JsonNode.Parse(style)
            };
            var walEntry = new JsonArray { (JsonNode)walObj };
            var bytes = session.ToBytes();
            tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString(), null, bytes);
            sync.MaybeAutoSave(tenant.TenantId, doc_id, bytes);

            return $"Styled {paragraphs.Count} paragraph(s).";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"styling paragraph in '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    [McpServerTool(Name = "style_table"), Description(
        "Apply table, row, and/or cell formatting using merge semantics.\n\n" +
        "Table style properties:\n" +
        "  border_style — single, double, dashed, dotted, none, thick\n" +
        "  border_size — integer (default 4)\n" +
        "  width — integer, width_type — pct/dxa/auto\n" +
        "  table_style — style name\n" +
        "  table_alignment — left, center, right\n\n" +
        "Cell style (applied to ALL cells in matched tables):\n" +
        "  shading — hex color, vertical_align — top/center/bottom\n" +
        "  width — integer, borders — {top, bottom, left, right}\n\n" +
        "Row style (applied to ALL rows in matched tables):\n" +
        "  height — integer, is_header — true/false\n\n" +
        "At least one of style, cell_style, or row_style must be provided.\n" +
        "Omit path to style ALL tables in the document.\n" +
        "Use [id='...'] for stable targeting (e.g. /body/table[id='1A2B3C4D']).")]
    public static string StyleTable(
        TenantScope tenant,
        SyncManager sync,
        [Description("Session ID of the document.")] string doc_id,
        [Description("JSON object of table-level style properties to merge.")] string? style = null,
        [Description("JSON object of cell-level style properties to merge (applied to ALL cells).")] string? cell_style = null,
        [Description("JSON object of row-level style properties to merge (applied to ALL rows).")] string? row_style = null,
        [Description("Optional typed path. Omit to style all tables in the document.")] string? path = null)
    {
        try
        {
            if (style is null && cell_style is null && row_style is null)
                return "Error: At least one of style, cell_style, or row_style must be provided.";

            var session = tenant.Sessions.Get(doc_id);
            var doc = session.Document;
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new InvalidOperationException("Document has no body.");

            JsonElement? styleEl = null, cellStyleEl = null, rowStyleEl = null;
            try
            {
                if (style is not null)
                {
                    var parsed = JsonDocument.Parse(style).RootElement;
                    if (parsed.ValueKind != JsonValueKind.Object)
                        return "Error: style must be a JSON object.";
                    styleEl = parsed;
                }
                if (cell_style is not null)
                {
                    var parsed = JsonDocument.Parse(cell_style).RootElement;
                    if (parsed.ValueKind != JsonValueKind.Object)
                        return "Error: cell_style must be a JSON object.";
                    cellStyleEl = parsed;
                }
                if (row_style is not null)
                {
                    var parsed = JsonDocument.Parse(row_style).RootElement;
                    if (parsed.ValueKind != JsonValueKind.Object)
                        return "Error: row_style must be a JSON object.";
                    rowStyleEl = parsed;
                }
            }
            catch (JsonException ex)
            {
                return $"Error: Invalid JSON — {ex.Message}";
            }

            List<Table> tables;
            if (path is null)
            {
                tables = body.Descendants<Table>().ToList();
            }
            else
            {
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

                tables = new List<Table>();
                foreach (var el in elements)
                {
                    tables.AddRange(StyleHelper.CollectTables(el));
                }
            }

            if (tables.Count == 0)
                return "No tables found to style.";

            foreach (var table in tables)
            {
                if (styleEl.HasValue)
                    StyleHelper.MergeTableProperties(table, styleEl.Value);

                if (cellStyleEl.HasValue)
                {
                    foreach (var cell in table.Descendants<TableCell>())
                    {
                        StyleHelper.MergeTableCellProperties(cell, cellStyleEl.Value);
                    }
                }

                if (rowStyleEl.HasValue)
                {
                    foreach (var row in table.Elements<TableRow>())
                    {
                        StyleHelper.MergeTableRowProperties(row, rowStyleEl.Value);
                    }
                }
            }

            // Append to WAL
            var walObj = new JsonObject
            {
                ["op"] = "style_table",
                ["path"] = path
            };
            if (style is not null)
                walObj["style"] = JsonNode.Parse(style);
            if (cell_style is not null)
                walObj["cell_style"] = JsonNode.Parse(cell_style);
            if (row_style is not null)
                walObj["row_style"] = JsonNode.Parse(row_style);

            var walEntry = new JsonArray { (JsonNode)walObj };
            var bytes = session.ToBytes();
            tenant.Sessions.AppendWal(doc_id, walEntry.ToJsonString(), null, bytes);
            sync.MaybeAutoSave(tenant.TenantId, doc_id, bytes);

            return $"Styled {tables.Count} table(s).";
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"styling table in '{doc_id}'"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    // --- Replay methods for WAL ---

    internal static void ReplayStyleElement(JsonElement patch, WordprocessingDocument doc)
    {
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Document has no body.");
        var styleEl = patch.GetProperty("style");

        string? path = null;
        if (patch.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
            path = pathEl.GetString();

        List<Run> runs;
        if (path is null)
        {
            runs = body.Descendants<Run>().ToList();
        }
        else
        {
            var parsed = DocxPath.Parse(path);
            var elements = PathResolver.Resolve(parsed, doc);
            runs = new List<Run>();
            foreach (var el in elements)
                runs.AddRange(StyleHelper.CollectRuns(el));
        }

        var trackChanges = RevisionHelper.IsTrackChangesEnabled(doc);
        foreach (var run in runs)
        {
            if (trackChanges)
            {
                var newProps = ElementFactory.CreateRunProperties(styleEl);
                RevisionHelper.ApplyRunPropertiesWithTracking(doc, run, newProps);
            }
            else
            {
                StyleHelper.MergeRunProperties(run, styleEl);
            }
        }
    }

    internal static void ReplayStyleParagraph(JsonElement patch, WordprocessingDocument doc)
    {
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Document has no body.");
        var styleEl = patch.GetProperty("style");

        string? path = null;
        if (patch.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
            path = pathEl.GetString();

        List<Paragraph> paragraphs;
        if (path is null)
        {
            paragraphs = body.Descendants<Paragraph>().ToList();
        }
        else
        {
            var parsed = DocxPath.Parse(path);
            var elements = PathResolver.Resolve(parsed, doc);
            paragraphs = new List<Paragraph>();
            foreach (var el in elements)
                paragraphs.AddRange(StyleHelper.CollectParagraphs(el));
        }

        var trackChanges = RevisionHelper.IsTrackChangesEnabled(doc);
        foreach (var para in paragraphs)
        {
            if (trackChanges)
            {
                var newProps = ElementFactory.CreateParagraphProperties(styleEl);
                RevisionHelper.ApplyParagraphPropertiesWithTracking(doc, para, newProps);
            }
            else
            {
                StyleHelper.MergeParagraphProperties(para, styleEl);
            }
        }
    }

    internal static void ReplayStyleTable(JsonElement patch, WordprocessingDocument doc)
    {
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Document has no body.");

        string? path = null;
        if (patch.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
            path = pathEl.GetString();

        List<Table> tables;
        if (path is null)
        {
            tables = body.Descendants<Table>().ToList();
        }
        else
        {
            var parsed = DocxPath.Parse(path);
            var elements = PathResolver.Resolve(parsed, doc);
            tables = new List<Table>();
            foreach (var el in elements)
                tables.AddRange(StyleHelper.CollectTables(el));
        }

        foreach (var table in tables)
        {
            if (patch.TryGetProperty("style", out var styleEl))
                StyleHelper.MergeTableProperties(table, styleEl);

            if (patch.TryGetProperty("cell_style", out var cellStyleEl))
            {
                foreach (var cell in table.Descendants<TableCell>())
                    StyleHelper.MergeTableCellProperties(cell, cellStyleEl);
            }

            if (patch.TryGetProperty("row_style", out var rowStyleEl))
            {
                foreach (var row in table.Elements<TableRow>())
                    StyleHelper.MergeTableRowProperties(row, rowStyleEl);
            }
        }
    }
}
