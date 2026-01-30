using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class ReadSectionTool
{
    [McpServerTool(Name = "read_section"), Description(
        "Read the content of a document section by index. " +
        "A section is a range of body elements delimited by SectionProperties.\n\n" +
        "Use this tool for direct access to a specific portion of the document " +
        "without loading the entire body.\n\n" +
        "Call with section_index omitted (or -1) to list all sections with their element counts and heading previews. " +
        "Then call again with a specific section_index to read its content.\n\n" +
        "Results are paginated: max 50 elements per call. Use offset to paginate within large sections.")]
    public static string ReadSection(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Zero-based section index. Omit or use -1 to list all sections.")] int? section_index = null,
        [Description("Output format: json, text, or summary. Default: json.")] string? format = "json",
        [Description("Number of elements to skip. Negative values count from the end (e.g. -10 = last 10 elements). Default: 0.")] int? offset = null,
        [Description("Maximum number of elements to return (1-50). Default: 50.")] int? limit = null)
    {
        var session = sessions.Get(doc_id);
        var doc = session.Document;
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Document has no body.");

        var sections = BuildSections(body);

        // List mode: return section overview
        if (section_index is null or -1)
        {
            return ListSections(sections);
        }

        var idx = section_index.Value;
        if (idx < 0 || idx >= sections.Count)
            return $"Error: Section index {idx} out of range. Document has {sections.Count} section(s) (0..{sections.Count - 1}).";

        var sectionElements = sections[idx].Elements;
        var totalCount = sectionElements.Count;

        var rawOffset = offset ?? 0;
        // Negative offset counts from the end: -10 means start at (total - 10)
        var effectiveOffset = rawOffset < 0 ? Math.Max(0, totalCount + rawOffset) : rawOffset;
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 50);

        if (effectiveOffset >= totalCount)
            return $"{{\"section\": {idx}, \"total\": {totalCount}, \"offset\": {effectiveOffset}, \"limit\": {effectiveLimit}, \"items\": []}}";

        var page = sectionElements
            .Skip(effectiveOffset)
            .Take(effectiveLimit)
            .ToList();

        var fmt = format?.ToLowerInvariant() ?? "json";
        var formatted = fmt switch
        {
            "json" => FormatJson(page),
            "text" => FormatText(page),
            "summary" => FormatSummary(page),
            _ => FormatJson(page)
        };

        if (fmt == "json")
        {
            return $"{{\"section\": {idx}, \"total\": {totalCount}, \"offset\": {effectiveOffset}, " +
                   $"\"limit\": {effectiveLimit}, \"count\": {page.Count}, \"items\": {formatted}}}";
        }

        return $"[Section {idx}: {page.Count}/{totalCount} elements, offset {effectiveOffset}]\n{formatted}";
    }

    private record SectionInfo(int Index, List<OpenXmlElement> Elements, string? FirstHeading);

    private static List<SectionInfo> BuildSections(Body body)
    {
        var sections = new List<SectionInfo>();
        var currentElements = new List<OpenXmlElement>();
        int sectionIdx = 0;

        foreach (var child in body.ChildElements)
        {
            // In OOXML, a paragraph can contain a SectionProperties in its ParagraphProperties
            // to mark the end of a section (all sections except the last).
            // The last section's properties are a direct child of Body.
            if (child is Paragraph p && p.ParagraphProperties?.SectionProperties is not null)
            {
                // This paragraph ends a section — include it in current section
                currentElements.Add(child);
                var heading = FindFirstHeading(currentElements);
                sections.Add(new SectionInfo(sectionIdx++, currentElements, heading));
                currentElements = new List<OpenXmlElement>();
            }
            else if (child is SectionProperties)
            {
                // Last section ends with a direct SectionProperties child of Body
                var heading = FindFirstHeading(currentElements);
                sections.Add(new SectionInfo(sectionIdx++, currentElements, heading));
                currentElements = new List<OpenXmlElement>();
            }
            else
            {
                currentElements.Add(child);
            }
        }

        // If there are remaining elements (body with no explicit final SectionProperties), add them
        if (currentElements.Count > 0)
        {
            var heading = FindFirstHeading(currentElements);
            sections.Add(new SectionInfo(sectionIdx, currentElements, heading));
        }

        return sections;
    }

    private static string? FindFirstHeading(List<OpenXmlElement> elements)
    {
        foreach (var el in elements)
        {
            if (el is Paragraph p && p.IsHeading())
                return Truncate(p.InnerText, 80);
        }
        return null;
    }

    private static string ListSections(List<SectionInfo> sections)
    {
        var arr = new JsonArray();
        foreach (var s in sections)
        {
            var obj = new JsonObject
            {
                ["index"] = s.Index,
                ["element_count"] = s.Elements.Count,
            };

            if (s.FirstHeading is not null)
                obj["first_heading"] = s.FirstHeading;

            // Element type breakdown
            var paragraphs = s.Elements.Count(e => e is Paragraph p && !p.IsHeading());
            var headings = s.Elements.Count(e => e is Paragraph p && p.IsHeading());
            var tables = s.Elements.Count(e => e is Table);

            var breakdown = new JsonObject();
            if (paragraphs > 0) breakdown["paragraphs"] = paragraphs;
            if (headings > 0) breakdown["headings"] = headings;
            if (tables > 0) breakdown["tables"] = tables;
            obj["breakdown"] = breakdown;

            arr.Add((JsonNode)obj);
        }

        var result = new JsonObject
        {
            ["section_count"] = sections.Count,
            ["sections"] = arr,
        };

        return result.ToJsonString(JsonOpts);
    }

    private static string FormatJson(List<OpenXmlElement> elements)
    {
        return QueryTool.FormatJsonArray(elements);
    }

    private static string FormatText(List<OpenXmlElement> elements)
    {
        return string.Join("\n", elements.Select(e => e.InnerText));
    }

    private static string FormatSummary(List<OpenXmlElement> elements)
    {
        var lines = new List<string> { $"Matched {elements.Count} element(s):" };
        foreach (var el in elements)
        {
            var desc = DescribeElement(el);
            if (desc is not null)
                lines.Add($"  - {desc}");
        }
        return string.Join("\n", lines);
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
