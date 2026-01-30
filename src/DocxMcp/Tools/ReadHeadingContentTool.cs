using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class ReadHeadingContentTool
{
    [McpServerTool(Name = "read_heading_content"), Description(
        "Read the content under a specific heading, including all sub-headings and their content. " +
        "This avoids traversing the entire document when you only need one section.\n\n" +
        "The tool collects every element from the target heading up to (but not including) " +
        "the next heading at the same or higher level.\n\n" +
        "Call with no heading_text and no heading_index to list all headings with their hierarchy " +
        "and content element counts. Then call again targeting a specific heading.\n\n" +
        "Results are paginated: max 50 elements per call. Use offset to paginate within large heading blocks.")]
    public static string ReadHeadingContent(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Text to search for in heading content (case-insensitive partial match). " +
                     "Omit to list all headings.")] string? heading_text = null,
        [Description("Zero-based index of the heading among all headings (or among headings at the specified level). " +
                     "Omit to list all headings.")] int? heading_index = null,
        [Description("Filter headings by level (1-9). Applies to both listing and content retrieval.")] int? heading_level = null,
        [Description("When true (default), content under sub-headings is included. " +
                     "When false, only content directly under the target heading (up to the first sub-heading) is returned.")] bool include_sub_headings = true,
        [Description("Output format: json, text, or summary. Default: json.")] string? format = "json",
        [Description("Number of elements to skip. Negative values count from the end. Default: 0.")] int? offset = null,
        [Description("Maximum number of elements to return (1-50). Default: 50.")] int? limit = null)
    {
        var session = sessions.Get(doc_id);
        var body = session.GetBody();

        var allChildren = body.ChildElements.Cast<OpenXmlElement>().ToList();

        // List mode: return heading hierarchy
        if (heading_text is null && heading_index is null)
        {
            return ListHeadings(allChildren, heading_level);
        }

        // Find the target heading
        var headingParagraph = FindHeading(allChildren, heading_text, heading_index, heading_level);
        if (headingParagraph is null)
        {
            return heading_text is not null
                ? $"Error: No heading found matching text '{heading_text}'" +
                  (heading_level.HasValue ? $" at level {heading_level.Value}" : "") + "."
                : $"Error: Heading index {heading_index} out of range" +
                  (heading_level.HasValue ? $" at level {heading_level.Value}" : "") + ".";
        }

        // Collect content under this heading
        var elements = CollectHeadingContent(allChildren, headingParagraph, include_sub_headings);
        var totalCount = elements.Count;

        // Apply pagination
        var rawOffset = offset ?? 0;
        var effectiveOffset = rawOffset < 0 ? Math.Max(0, totalCount + rawOffset) : rawOffset;
        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 50);

        if (effectiveOffset >= totalCount)
        {
            var headingInfo = BuildHeadingInfo(headingParagraph);
            headingInfo["total"] = totalCount;
            headingInfo["offset"] = effectiveOffset;
            headingInfo["limit"] = effectiveLimit;
            headingInfo["items"] = new JsonArray();
            return headingInfo.ToJsonString(JsonOpts);
        }

        var page = elements
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
            var result = BuildHeadingInfo(headingParagraph);
            result["total"] = totalCount;
            result["offset"] = effectiveOffset;
            result["limit"] = effectiveLimit;
            result["count"] = page.Count;
            result["items"] = JsonNode.Parse(formatted);
            return result.ToJsonString(JsonOpts);
        }

        var headingLevel = headingParagraph.GetHeadingLevel();
        var headingTextValue = headingParagraph.InnerText;
        return $"[Heading {headingLevel}: \"{headingTextValue}\" — {page.Count}/{totalCount} elements, offset {effectiveOffset}]\n{formatted}";
    }

    private static JsonObject BuildHeadingInfo(Paragraph heading)
    {
        return new JsonObject
        {
            ["heading_level"] = heading.GetHeadingLevel(),
            ["heading_text"] = heading.InnerText,
        };
    }

    internal static Paragraph? FindHeading(
        List<OpenXmlElement> allChildren,
        string? headingText,
        int? headingIndex,
        int? headingLevel)
    {
        var headings = allChildren
            .OfType<Paragraph>()
            .Where(p => p.IsHeading())
            .Where(p => !headingLevel.HasValue || p.GetHeadingLevel() == headingLevel.Value)
            .ToList();

        if (headingText is not null)
        {
            return headings.FirstOrDefault(h =>
                h.InnerText.Contains(headingText, StringComparison.OrdinalIgnoreCase));
        }

        if (headingIndex.HasValue)
        {
            var idx = headingIndex.Value;
            if (idx < 0 || idx >= headings.Count)
                return null;
            return headings[idx];
        }

        return null;
    }

    internal static List<OpenXmlElement> CollectHeadingContent(
        List<OpenXmlElement> allChildren,
        Paragraph headingParagraph,
        bool includeSubHeadings)
    {
        var headingLevel = headingParagraph.GetHeadingLevel();
        var startIndex = allChildren.IndexOf(headingParagraph);
        if (startIndex < 0)
            return new List<OpenXmlElement>();

        var elements = new List<OpenXmlElement> { headingParagraph };

        for (int i = startIndex + 1; i < allChildren.Count; i++)
        {
            var child = allChildren[i];

            if (child is Paragraph p && p.IsHeading())
            {
                var childLevel = p.GetHeadingLevel();

                // Stop at same or higher level heading (lower number = higher level)
                if (childLevel <= headingLevel)
                    break;

                // If not including sub-headings, stop at first sub-heading
                if (!includeSubHeadings)
                    break;
            }

            // Skip SectionProperties (they are structural, not content)
            if (child is SectionProperties)
                continue;

            elements.Add(child);
        }

        return elements;
    }

    private static string ListHeadings(List<OpenXmlElement> allChildren, int? filterLevel)
    {
        var headings = new List<(Paragraph Paragraph, int Index, int ContentCount, List<string> SubHeadings)>();
        var allParagraphs = allChildren.OfType<Paragraph>().Where(p => p.IsHeading()).ToList();

        for (int h = 0; h < allParagraphs.Count; h++)
        {
            var heading = allParagraphs[h];
            var level = heading.GetHeadingLevel();

            if (filterLevel.HasValue && level != filterLevel.Value)
                continue;

            // Count content elements under this heading
            var content = CollectHeadingContent(allChildren, heading, includeSubHeadings: true);
            var contentCount = content.Count - 1; // Exclude the heading itself

            // Find direct sub-headings
            var subHeadings = new List<string>();
            var startIdx = allChildren.IndexOf(heading);
            for (int i = startIdx + 1; i < allChildren.Count; i++)
            {
                var child = allChildren[i];
                if (child is Paragraph p && p.IsHeading())
                {
                    var childLevel = p.GetHeadingLevel();
                    if (childLevel <= level)
                        break;
                    if (childLevel == level + 1)
                        subHeadings.Add(p.InnerText);
                }
            }

            headings.Add((heading, h, contentCount, subHeadings));
        }

        var arr = new JsonArray();
        foreach (var (paragraph, index, contentCount, subHeadings) in headings)
        {
            var obj = new JsonObject
            {
                ["index"] = index,
                ["level"] = paragraph.GetHeadingLevel(),
                ["text"] = paragraph.InnerText,
                ["content_elements"] = contentCount,
            };

            if (subHeadings.Count > 0)
            {
                var subArr = new JsonArray();
                foreach (var sub in subHeadings)
                    subArr.Add((JsonNode)JsonValue.Create(sub)!);
                obj["direct_sub_headings"] = subArr;
            }

            arr.Add((JsonNode)obj);
        }

        var result = new JsonObject
        {
            ["heading_count"] = arr.Count,
            ["headings"] = arr,
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
        _ => null
    };

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };
}
