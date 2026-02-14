using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;
using DocxMcp.Paths;
using static DocxMcp.Helpers.ElementIdManager;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class QueryTool
{
    [McpServerTool(Name = "query"), Description(
        "Read any part of a document using typed paths. " +
        "Returns structured JSON, plain text, or a summary depending on the format parameter.\n\n" +
        "IMPORTANT: Prefer direct access with indexed paths (e.g. /body/paragraph[0], /body/table[2]) " +
        "over wildcard queries. Use count_elements first to know how many elements exist, " +
        "then access them individually or in small ranges.\n\n" +
        "When using wildcard [*] selectors, results are paginated with a maximum of 50 elements per call. " +
        "Use offset and limit to paginate through large result sets.\n\n" +
        "Path examples:\n" +
        "  /body — full document structure summary\n" +
        "  /body/paragraph[0] — first paragraph (preferred: direct access)\n" +
        "  /body/table[0] — first table (preferred: direct access)\n" +
        "  /body/paragraph[*] — all paragraphs (paginated, max 50)\n" +
        "  /body/heading[*] — all headings (paginated, max 50)\n" +
        "  /body/paragraph[text~='hello'] — paragraphs containing 'hello'\n" +
        "  /body/paragraph[id='1A2B3C4D'] — element by stable ID\n" +
        "  /metadata — document properties\n" +
        "  /styles — style definitions\n\n" +
        "Every element has a stable 'id' field in JSON output. Use [id='...'] selectors for precise targeting.")]
    public static string Query(
        TenantScope tenant,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Typed path to query (e.g. /body/paragraph[0], /body/table[0]). Prefer direct indexed access.")] string path,
        [Description("Output format: json, text, or summary. Default: json.")] string? format = "json",
        [Description("Number of elements to skip. Negative values count from the end (e.g. -10 = last 10 elements). Default: 0.")] int? offset = null,
        [Description("Maximum number of elements to return (1-50). Default: 50.")] int? limit = null)
    {
        var session = tenant.Sessions.Get(doc_id);
        var doc = session.Document;

        // Handle special paths
        if (path is "/metadata" or "metadata")
            return QueryMetadata(doc);
        if (path is "/styles" or "styles")
            return QueryStyles(doc);
        if (path is "/body" or "body" or "/")
            return QueryBodySummary(doc);

        var parsed = DocxPath.Parse(path);
        var elements = PathResolver.Resolve(parsed, doc);

        // Apply pagination when multiple elements are returned
        var totalCount = elements.Count;
        if (totalCount > 1)
        {
            var rawOffset = offset ?? 0;
            // Negative offset counts from the end: -10 means start at (total - 10)
            var effectiveOffset = rawOffset < 0 ? Math.Max(0, totalCount + rawOffset) : rawOffset;
            var effectiveLimit = Math.Clamp(limit ?? 50, 1, 50);

            if (effectiveOffset >= totalCount)
                return $"{{\"total\": {totalCount}, \"offset\": {effectiveOffset}, \"limit\": {effectiveLimit}, \"items\": []}}";

            elements = elements
                .Skip(effectiveOffset)
                .Take(effectiveLimit)
                .ToList();

            // Wrap result with pagination metadata
            var formatted = (format?.ToLowerInvariant() ?? "json") switch
            {
                "json" => FormatJsonArray(elements, doc),
                "text" => FormatText(elements),
                "summary" => FormatSummary(elements),
                _ => FormatJsonArray(elements, doc)
            };

            if ((format?.ToLowerInvariant() ?? "json") == "json")
            {
                return $"{{\"total\": {totalCount}, \"offset\": {effectiveOffset}, \"limit\": {effectiveLimit}, " +
                       $"\"count\": {elements.Count}, \"items\": {formatted}}}";
            }

            return $"[{elements.Count}/{totalCount} elements, offset {effectiveOffset}]\n{formatted}";
        }

        return (format?.ToLowerInvariant() ?? "json") switch
        {
            "json" => FormatJson(elements, doc),
            "text" => FormatText(elements),
            "summary" => FormatSummary(elements),
            _ => FormatJson(elements, doc)
        };
    }

    private static string QueryMetadata(WordprocessingDocument doc)
    {
        var props = doc.PackageProperties;
        var result = new JsonObject
        {
            ["title"] = props.Title,
            ["subject"] = props.Subject,
            ["creator"] = props.Creator,
            ["description"] = props.Description,
            ["lastModifiedBy"] = props.LastModifiedBy,
            ["created"] = props.Created?.ToString("o"),
            ["modified"] = props.Modified?.ToString("o"),
        };

        return result.ToJsonString(JsonOpts);
    }

    private static string QueryStyles(WordprocessingDocument doc)
    {
        var stylesPart = doc.MainDocumentPart?.StyleDefinitionsPart;
        if (stylesPart?.Styles is null)
            return "[]";

        var arr = new JsonArray();
        foreach (var s in stylesPart.Styles.Elements<Style>())
        {
            arr.Add((JsonNode)new JsonObject
            {
                ["id"] = s.StyleId?.Value,
                ["name"] = s.StyleName?.Val?.Value,
                ["type"] = s.Type?.Value.ToString(),
                ["basedOn"] = s.BasedOn?.Val?.Value,
            });
        }

        return arr.ToJsonString(JsonOpts);
    }

    private static string QueryBodySummary(WordprocessingDocument doc)
    {
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
            return """{"error": "Document has no body."}""";

        var paragraphs = body.Elements<Paragraph>().ToList();
        var tables = body.Elements<Table>().ToList();
        var headings = paragraphs.Where(p => p.IsHeading()).ToList();

        var headingsArr = new JsonArray();
        foreach (var h in headings)
        {
            var hObj = new JsonObject();
            var hId = GetId(h);
            if (hId is not null)
                hObj["id"] = hId;
            hObj["level"] = h.GetHeadingLevel();
            hObj["text"] = h.InnerText;
            headingsArr.Add((JsonNode)hObj);
        }

        var structureArr = new JsonArray();
        foreach (var e in body.ChildElements)
        {
            var desc = DescribeElement(e);
            if (desc is not null)
                structureArr.Add((JsonNode?)JsonValue.Create(desc));
        }

        var summary = new JsonObject
        {
            ["paragraph_count"] = paragraphs.Count,
            ["table_count"] = tables.Count,
            ["heading_count"] = headings.Count,
            ["headings"] = headingsArr,
            ["structure"] = structureArr,
        };

        return summary.ToJsonString(JsonOpts);
    }

    private static string FormatJson(List<OpenXmlElement> elements, WordprocessingDocument? doc = null)
    {
        if (elements.Count == 1)
            return ElementToJson(elements[0], doc).ToJsonString(JsonOpts);

        return FormatJsonArray(elements, doc);
    }

    /// <summary>
    /// Always returns a JSON array, even for a single element.
    /// Used by pagination envelopes where items must be an array.
    /// </summary>
    internal static string FormatJsonArray(List<OpenXmlElement> elements, WordprocessingDocument? doc = null)
    {
        var arr = new JsonArray();
        foreach (var el in elements)
            arr.Add((JsonNode?)ElementToJson(el, doc));

        return arr.ToJsonString(JsonOpts);
    }

    private static string FormatText(List<OpenXmlElement> elements)
    {
        var sb = new StringBuilder();
        foreach (var e in elements)
        {
            var id = GetId(e);
            if (id is not null)
                sb.Append($"[{id}] ");
            sb.AppendLine(e.InnerText);
        }
        return sb.ToString();
    }

    private static string FormatSummary(List<OpenXmlElement> elements)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Matched {elements.Count} element(s):");
        foreach (var el in elements)
        {
            var desc = DescribeElement(el);
            if (desc is not null)
                sb.AppendLine($"  - {desc}");
        }
        return sb.ToString();
    }

    internal static JsonNode ElementToJson(OpenXmlElement element, WordprocessingDocument? doc = null) => element switch
    {
        Paragraph p => ParagraphToJson(p, doc),
        Table t => TableToJson(t),
        TableRow tr => RowToJson(tr),
        TableCell tc => CellToJson(tc),
        Run r => RunToJson(r),
        Hyperlink h => HyperlinkToJson(h),
        ParagraphProperties pp => ParagraphPropsToJson(pp),
        RunProperties rp => RunPropsToJson(rp),
        _ => GenericElementToJson(element)
    };

    private static JsonObject ParagraphToJson(Paragraph p, WordprocessingDocument? doc = null)
    {
        var result = new JsonObject { ["type"] = "paragraph" };

        var paraId = GetId(p);
        if (paraId is not null)
            result["id"] = paraId;

        if (p.IsHeading())
        {
            result["type"] = "heading";
            result["level"] = p.GetHeadingLevel();
        }

        result["text"] = p.InnerText;

        var styleId = p.GetStyleId();
        if (styleId is not null)
            result["style"] = styleId;

        // Paragraph-level properties
        if (p.ParagraphProperties is ParagraphProperties pp)
        {
            var propsObj = new JsonObject();
            bool hasProperties = false;

            if (pp.Justification?.Val is not null)
            {
                propsObj["alignment"] = pp.Justification.Val.InnerText;
                hasProperties = true;
            }

            if (pp.SpacingBetweenLines is SpacingBetweenLines spacing)
            {
                if (spacing.Before?.Value is string sb)
                {
                    propsObj["spacing_before"] = int.TryParse(sb, out var v) ? v : 0;
                    hasProperties = true;
                }
                if (spacing.After?.Value is string sa)
                {
                    propsObj["spacing_after"] = int.TryParse(sa, out var v) ? v : 0;
                    hasProperties = true;
                }
                if (spacing.Line?.Value is string sl)
                {
                    propsObj["line_spacing"] = int.TryParse(sl, out var v) ? v : 0;
                    hasProperties = true;
                }
            }

            if (pp.Indentation is Indentation indent)
            {
                if (indent.Left?.Value is string il)
                {
                    propsObj["indent_left"] = int.TryParse(il, out var v) ? v : 0;
                    hasProperties = true;
                }
                if (indent.Right?.Value is string ir)
                {
                    propsObj["indent_right"] = int.TryParse(ir, out var v) ? v : 0;
                    hasProperties = true;
                }
                if (indent.FirstLine?.Value is string ifl)
                {
                    propsObj["indent_first_line"] = int.TryParse(ifl, out var v) ? v : 0;
                    hasProperties = true;
                }
                if (indent.Hanging?.Value is string ih)
                {
                    propsObj["indent_hanging"] = int.TryParse(ih, out var v) ? v : 0;
                    hasProperties = true;
                }
            }

            if (pp.Tabs is Tabs tabs)
            {
                var tabsArr = new JsonArray();
                foreach (var tab in tabs.Elements<TabStop>())
                {
                    var tabObj = new JsonObject();
                    if (tab.Position?.Value is int pos)
                        tabObj["position"] = pos;
                    if (tab.Val is not null)
                        tabObj["alignment"] = tab.Val.InnerText;
                    if (tab.Leader is not null)
                        tabObj["leader"] = tab.Leader.InnerText;
                    tabsArr.Add((JsonNode)tabObj);
                }
                propsObj["tabs"] = tabsArr;
                hasProperties = true;
            }

            if (hasProperties)
                result["properties"] = propsObj;
        }

        // Always emit runs array for round-trip fidelity
        var runs = p.Elements<Run>().ToList();
        if (runs.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var r in runs)
                arr.Add((JsonNode)RunToJson(r));
            result["runs"] = arr;
        }

        var hyperlinks = p.Elements<Hyperlink>().ToList();
        if (hyperlinks.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var h in hyperlinks)
                arr.Add((JsonNode)HyperlinkToJson(h));
            result["hyperlinks"] = arr;
        }

        // Enrich with comment metadata when document is available
        if (doc is not null)
        {
            var commentStarts = p.Descendants<CommentRangeStart>().ToList();
            if (commentStarts.Count > 0)
            {
                var commentsPart = doc.MainDocumentPart?.WordprocessingCommentsPart;
                if (commentsPart?.Comments is not null)
                {
                    var commentsArr = new JsonArray();
                    var body = doc.MainDocumentPart?.Document?.Body;

                    foreach (var cs in commentStarts)
                    {
                        var idStr = cs.Id?.Value;
                        if (idStr is null) continue;

                        var comment = commentsPart.Comments.Elements<Comment>()
                            .FirstOrDefault(c => c.Id?.Value == idStr);
                        if (comment is null) continue;

                        var commentObj = new JsonObject
                        {
                            ["id"] = int.TryParse(idStr, out var cid) ? cid : 0,
                            ["author"] = comment.Author?.Value ?? "",
                            ["text"] = string.Join("\n", comment.Elements<Paragraph>().Select(cp => cp.InnerText)),
                        };

                        if (body is not null)
                        {
                            var anchoredText = Helpers.CommentHelper.GetAnchoredText(p, idStr);
                            if (anchoredText is not null)
                                commentObj["anchored_text"] = anchoredText;
                        }

                        commentsArr.Add((JsonNode)commentObj);
                    }

                    if (commentsArr.Count > 0)
                        result["comments"] = commentsArr;
                }
            }
        }

        return result;
    }

    private static JsonObject TableToJson(Table t)
    {
        var (rowCount, cols) = t.GetTableDimensions();

        var result = new JsonObject
        {
            ["type"] = "table",
        };

        var tableId = GetId(t);
        if (tableId is not null)
            result["id"] = tableId;

        result["rows"] = rowCount;
        result["cols"] = cols;

        // Table properties
        var tblProps = t.GetFirstChild<TableProperties>();
        if (tblProps is not null)
        {
            var propsObj = new JsonObject();
            bool hasProps = false;

            if (tblProps.TableStyle?.Val?.Value is string style)
            {
                propsObj["table_style"] = style;
                hasProps = true;
            }

            if (tblProps.TableWidth is TableWidth tw)
            {
                propsObj["width"] = tw.Width?.Value;
                if (tw.Type is not null)
                    propsObj["width_type"] = tw.Type.InnerText;
                hasProps = true;
            }

            if (tblProps.TableJustification?.Val is not null)
            {
                propsObj["table_alignment"] = tblProps.TableJustification.Val.InnerText;
                hasProps = true;
            }

            if (hasProps)
                result["properties"] = propsObj;
        }

        // Data: simple text array for backwards compatibility
        var dataArr = new JsonArray();
        foreach (var row in t.Elements<TableRow>())
        {
            var rowArr = new JsonArray();
            foreach (var cell in row.Elements<TableCell>())
                rowArr.Add((JsonNode?)JsonValue.Create(cell.InnerText));
            dataArr.Add((JsonNode)rowArr);
        }
        result["data"] = dataArr;

        // Rich row data with cell details
        var richRows = new JsonArray();
        foreach (var row in t.Elements<TableRow>())
            richRows.Add((JsonNode)RowToJson(row));
        result["rich_rows"] = richRows;

        return result;
    }

    private static JsonObject RowToJson(TableRow tr)
    {
        var result = new JsonObject { ["type"] = "row" };

        var rowId = GetId(tr);
        if (rowId is not null)
            result["id"] = rowId;

        // Simple cells for backwards compat
        var cellsArr = new JsonArray();
        foreach (var c in tr.Elements<TableCell>())
            cellsArr.Add((JsonNode?)JsonValue.Create(c.InnerText));
        result["cells"] = cellsArr;

        // Row properties
        if (tr.TableRowProperties is TableRowProperties trp)
        {
            var propsObj = new JsonObject();
            bool hasProps = false;

            if (trp.GetFirstChild<TableHeader>() is not null)
            {
                propsObj["is_header"] = true;
                hasProps = true;
            }

            if (trp.GetFirstChild<TableRowHeight>() is TableRowHeight h)
            {
                propsObj["height"] = (int)(h.Val?.Value ?? 0);
                hasProps = true;
            }

            if (hasProps)
                result["properties"] = propsObj;
        }

        // Rich cells with full detail
        var richCells = new JsonArray();
        foreach (var c in tr.Elements<TableCell>())
            richCells.Add((JsonNode)CellToJson(c));
        result["rich_cells"] = richCells;

        return result;
    }

    private static JsonObject CellToJson(TableCell tc)
    {
        var result = new JsonObject
        {
            ["type"] = "cell",
        };

        var cellId = GetId(tc);
        if (cellId is not null)
            result["id"] = cellId;

        result["text"] = tc.InnerText;

        // Cell properties
        if (tc.TableCellProperties is TableCellProperties tcp)
        {
            var propsObj = new JsonObject();
            bool hasProps = false;

            if (tcp.TableCellWidth is TableCellWidth w)
            {
                propsObj["width"] = w.Width?.Value;
                hasProps = true;
            }

            if (tcp.TableCellVerticalAlignment?.Val is not null)
            {
                propsObj["vertical_align"] = tcp.TableCellVerticalAlignment.Val.InnerText;
                hasProps = true;
            }

            if (tcp.Shading is Shading sh)
            {
                propsObj["shading"] = sh.Fill?.Value;
                hasProps = true;
            }

            if (tcp.GridSpan?.Val?.Value is int gs)
            {
                propsObj["col_span"] = gs;
                hasProps = true;
            }

            if (tcp.VerticalMerge is VerticalMerge vm)
            {
                propsObj["row_span"] = vm.Val?.Value == MergedCellValues.Restart ? "restart" : "continue";
                hasProps = true;
            }

            if (hasProps)
                result["properties"] = propsObj;
        }

        // Paragraphs (full detail)
        var parArr = new JsonArray();
        foreach (var p in tc.Elements<Paragraph>())
            parArr.Add((JsonNode)ParagraphToJson(p));
        result["paragraphs"] = parArr;

        return result;
    }

    private static JsonObject RunToJson(Run r)
    {
        var result = new JsonObject
        {
            ["type"] = "run",
        };

        var runId = GetId(r);
        if (runId is not null)
            result["id"] = runId;

        // Detect tab characters
        if (r.GetFirstChild<TabChar>() is not null)
        {
            result["tab"] = true;
            result["text"] = "\t";
        }
        else if (r.GetFirstChild<Break>() is Break brk)
        {
            var breakType = brk.Type?.Value;
            string breakName;
            if (breakType is not null && breakType == BreakValues.Page)
                breakName = "page";
            else if (breakType is not null && breakType == BreakValues.Column)
                breakName = "column";
            else
                breakName = "line";
            result["break"] = breakName;
            result["text"] = "";
        }
        else
        {
            result["text"] = r.InnerText;
        }

        if (r.RunProperties is not null)
            result["style"] = RunPropsToJson(r.RunProperties);

        return result;
    }

    private static JsonObject HyperlinkToJson(Hyperlink h)
    {
        var result = new JsonObject
        {
            ["type"] = "hyperlink",
        };

        var hlId = GetId(h);
        if (hlId is not null)
            result["id"] = hlId;

        result["text"] = h.InnerText;
        result["rel_id"] = h.Id?.Value ?? "";
        return result;
    }

    private static JsonObject ParagraphPropsToJson(ParagraphProperties pp)
    {
        var result = new JsonObject { ["type"] = "paragraph_properties" };

        if (pp.ParagraphStyleId?.Val?.Value is string styleId)
            result["style_id"] = styleId;
        if (pp.Justification?.Val is not null)
            result["alignment"] = pp.Justification.Val.InnerText;

        return result;
    }

    private static JsonObject RunPropsToJson(RunProperties rp)
    {
        var result = new JsonObject { ["type"] = "run_properties" };

        if (rp.Bold is not null) result["bold"] = true;
        if (rp.Italic is not null) result["italic"] = true;
        if (rp.Underline is not null) result["underline"] = true;
        if (rp.Strike is not null) result["strike"] = true;
        if (rp.FontSize?.Val?.Value is string fs) result["font_size"] = int.Parse(fs) / 2;
        if (rp.RunFonts?.Ascii?.Value is string fn) result["font_name"] = fn;
        if (rp.Color?.Val?.Value is string c) result["color"] = c;

        return result;
    }

    private static JsonObject GenericElementToJson(OpenXmlElement element)
    {
        var result = new JsonObject
        {
            ["type"] = element.GetType().Name,
        };

        var elemId = GetId(element);
        if (elemId is not null)
            result["id"] = elemId;

        result["text"] = element.InnerText;
        return result;
    }

    private static string? DescribeElement(OpenXmlElement element)
    {
        var id = GetId(element);
        var prefix = id is not null ? $"[{id}] " : "";

        return element switch
        {
            Paragraph p when p.IsHeading() =>
                $"{prefix}heading{p.GetHeadingLevel()}: \"{Truncate(p.InnerText, 60)}\"",
            Paragraph p =>
                $"{prefix}paragraph: \"{Truncate(p.InnerText, 60)}\"",
            Table t =>
                $"{prefix}table: {t.GetTableDimensions().Rows}x{t.GetTableDimensions().Cols}",
            SectionProperties =>
                "section_break",
            _ => null
        };
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}
