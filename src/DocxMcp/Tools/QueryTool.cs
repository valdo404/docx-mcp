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

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class QueryTool
{
    [McpServerTool(Name = "query"), Description(
        "Read any part of a document using typed paths. " +
        "Returns structured JSON, plain text, or a summary depending on the format parameter.\n\n" +
        "Path examples:\n" +
        "  /body — full document structure summary\n" +
        "  /body/paragraph[0] — first paragraph\n" +
        "  /body/paragraph[*] — all paragraphs\n" +
        "  /body/table[0] — first table\n" +
        "  /body/heading[*] — all headings\n" +
        "  /body/paragraph[text~='hello'] — paragraphs containing 'hello'\n" +
        "  /metadata — document properties\n" +
        "  /styles — style definitions")]
    public static string Query(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Typed path to query (e.g. /body, /body/paragraph[0], /body/table[*]).")] string path,
        [Description("Output format: json, text, or summary. Default: json.")] string? format = "json")
    {
        var session = sessions.Get(doc_id);
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

        return (format?.ToLowerInvariant() ?? "json") switch
        {
            "json" => FormatJson(elements),
            "text" => FormatText(elements),
            "summary" => FormatSummary(elements),
            _ => FormatJson(elements)
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
            headingsArr.Add((JsonNode)new JsonObject
            {
                ["level"] = h.GetHeadingLevel(),
                ["text"] = h.InnerText,
            });
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

    private static string FormatJson(List<OpenXmlElement> elements)
    {
        if (elements.Count == 1)
            return ElementToJson(elements[0]).ToJsonString(JsonOpts);

        var arr = new JsonArray();
        foreach (var el in elements)
            arr.Add((JsonNode?)ElementToJson(el));

        return arr.ToJsonString(JsonOpts);
    }

    private static string FormatText(List<OpenXmlElement> elements)
    {
        return string.Join("\n", elements.Select(e => e.InnerText));
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

    private static JsonNode ElementToJson(OpenXmlElement element) => element switch
    {
        Paragraph p => ParagraphToJson(p),
        Table t => TableToJson(t),
        TableRow tr => RowToJson(tr),
        TableCell tc => CellToJson(tc),
        Run r => RunToJson(r),
        Hyperlink h => HyperlinkToJson(h),
        ParagraphProperties pp => ParagraphPropsToJson(pp),
        RunProperties rp => RunPropsToJson(rp),
        _ => new JsonObject
        {
            ["type"] = element.GetType().Name,
            ["text"] = element.InnerText,
        }
    };

    private static JsonObject ParagraphToJson(Paragraph p)
    {
        var result = new JsonObject { ["type"] = "paragraph" };

        if (p.IsHeading())
        {
            result["type"] = "heading";
            result["level"] = p.GetHeadingLevel();
        }

        result["text"] = p.InnerText;

        var styleId = p.GetStyleId();
        if (styleId is not null)
            result["style"] = styleId;

        var runs = p.Elements<Run>().ToList();
        if (runs.Count > 1)
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

        return result;
    }

    private static JsonObject TableToJson(Table t)
    {
        var (rows, cols) = t.GetTableDimensions();
        var dataArr = new JsonArray();
        foreach (var row in t.Elements<TableRow>())
        {
            var rowArr = new JsonArray();
            foreach (var cell in row.Elements<TableCell>())
                rowArr.Add((JsonNode?)JsonValue.Create(cell.InnerText));
            dataArr.Add((JsonNode)rowArr);
        }

        return new JsonObject
        {
            ["type"] = "table",
            ["rows"] = rows,
            ["cols"] = cols,
            ["data"] = dataArr,
        };
    }

    private static JsonObject RowToJson(TableRow tr)
    {
        var cellsArr = new JsonArray();
        foreach (var c in tr.Elements<TableCell>())
            cellsArr.Add((JsonNode?)JsonValue.Create(c.InnerText));

        return new JsonObject
        {
            ["type"] = "row",
            ["cells"] = cellsArr,
        };
    }

    private static JsonObject CellToJson(TableCell tc)
    {
        var parArr = new JsonArray();
        foreach (var p in tc.Elements<Paragraph>())
            parArr.Add((JsonNode)ParagraphToJson(p));

        return new JsonObject
        {
            ["type"] = "cell",
            ["text"] = tc.InnerText,
            ["paragraphs"] = parArr,
        };
    }

    private static JsonObject RunToJson(Run r)
    {
        var result = new JsonObject
        {
            ["type"] = "run",
            ["text"] = r.InnerText,
        };

        if (r.RunProperties is not null)
            result["style"] = RunPropsToJson(r.RunProperties);

        return result;
    }

    private static JsonObject HyperlinkToJson(Hyperlink h)
    {
        return new JsonObject
        {
            ["type"] = "hyperlink",
            ["text"] = h.InnerText,
            ["id"] = h.Id?.Value ?? "",
        };
    }

    private static JsonObject ParagraphPropsToJson(ParagraphProperties pp)
    {
        var result = new JsonObject { ["type"] = "paragraph_properties" };

        if (pp.ParagraphStyleId?.Val?.Value is string styleId)
            result["style_id"] = styleId;
        if (pp.Justification?.Val?.Value is JustificationValues j)
            result["alignment"] = j.ToString().ToLowerInvariant();

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

    private static string? DescribeElement(OpenXmlElement element) => element switch
    {
        Paragraph p when p.IsHeading() =>
            $"heading{p.GetHeadingLevel()}: \"{Truncate(p.InnerText, 60)}\"",
        Paragraph p =>
            $"paragraph: \"{Truncate(p.InnerText, 60)}\"",
        Table t =>
            $"table: {t.GetTableDimensions().Rows}x{t.GetTableDimensions().Cols}",
        SectionProperties =>
            "section_break",
        _ => null
    };

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}
