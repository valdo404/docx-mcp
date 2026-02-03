using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxMcp.Diff;

/// <summary>
/// Captures the state of an element for comparison.
/// Does NOT rely on element IDs - uses content-based fingerprinting.
/// </summary>
public sealed class ElementSnapshot
{
    /// <summary>
    /// Content-based fingerprint for matching elements across documents.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// Type of element (paragraph, table, heading, etc.).
    /// </summary>
    public required string ElementType { get; init; }

    /// <summary>
    /// Position index in the parent's children list.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Path to this element in the document structure.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Plain text content of the element.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Outer XML representation for deep comparison.
    /// </summary>
    public required string OuterXml { get; init; }

    /// <summary>
    /// JSON representation for patch generation.
    /// </summary>
    public required JsonObject JsonValue { get; init; }

    /// <summary>
    /// Original OpenXML element reference.
    /// </summary>
    public OpenXmlElement? Element { get; init; }

    /// <summary>
    /// Child element snapshots (for hierarchical elements like tables).
    /// </summary>
    public List<ElementSnapshot> Children { get; init; } = [];

    /// <summary>
    /// Heading level (1-9) if this is a heading, null otherwise.
    /// </summary>
    public int? HeadingLevel { get; init; }

    /// <summary>
    /// Create a snapshot from an OpenXML element.
    /// </summary>
    public static ElementSnapshot FromElement(OpenXmlElement element, int index, string parentPath)
    {
        var elementType = GetElementTypeName(element);
        var text = element.InnerText;
        var path = $"{parentPath}/{elementType}[{index}]";
        var headingLevel = GetHeadingLevel(element);

        var snapshot = new ElementSnapshot
        {
            Fingerprint = ComputeFingerprint(element, elementType, text),
            ElementType = elementType,
            Index = index,
            Path = path,
            Text = text,
            OuterXml = element.OuterXml,
            JsonValue = ElementToJsonObject(element),
            Element = element,
            HeadingLevel = headingLevel
        };

        // Capture children for hierarchical elements
        if (element is Table table)
        {
            int rowIdx = 0;
            foreach (var row in table.Elements<TableRow>())
            {
                snapshot.Children.Add(FromElement(row, rowIdx++, path));
            }
        }
        else if (element is TableRow row)
        {
            int cellIdx = 0;
            foreach (var cell in row.Elements<TableCell>())
            {
                snapshot.Children.Add(FromElement(cell, cellIdx++, path));
            }
        }
        else if (element is Paragraph para)
        {
            int runIdx = 0;
            foreach (var run in para.Elements<Run>())
            {
                snapshot.Children.Add(FromElement(run, runIdx++, path));
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Compute a content-based fingerprint that doesn't depend on IDs.
    /// Two elements with the same content will have the same fingerprint.
    /// Uses EXACT text matching - whitespace differences ARE detected.
    /// </summary>
    private static string ComputeFingerprint(OpenXmlElement element, string elementType, string text)
    {
        var sb = new StringBuilder();
        sb.Append(elementType);
        sb.Append('|');

        // For headings, include the level
        if (element is Paragraph p && GetHeadingLevel(element) is int level)
        {
            sb.Append($"h{level}|");
        }

        // Include EXACT text content (whitespace matters)
        sb.Append(text);

        // For tables, include structure info
        if (element is Table table)
        {
            var rows = table.Elements<TableRow>().ToList();
            sb.Append($"|rows:{rows.Count}");
            if (rows.Count > 0)
            {
                var cols = rows[0].Elements<TableCell>().Count();
                sb.Append($"|cols:{cols}");
            }
        }

        // Compute hash
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Normalize text for comparison (trim, collapse whitespace).
    /// </summary>
    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Collapse multiple whitespace to single space
        var normalized = string.Join(' ', text.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
        return normalized.Trim();
    }

    /// <summary>
    /// Check if two snapshots have equivalent content.
    /// Uses EXACT comparison - whitespace differences matter.
    /// </summary>
    public bool ContentEquals(ElementSnapshot other)
    {
        // Different types = not equal
        if (ElementType != other.ElementType)
            return false;

        // Compare EXACT text (whitespace matters)
        if (Text != other.Text)
            return false;

        // For paragraphs, compare run structure
        if (ElementType == "paragraph" || ElementType == "heading")
        {
            return CompareParagraphContent(this, other);
        }

        // For tables, compare cell contents
        if (ElementType == "table")
        {
            return CompareTableContent(this, other);
        }

        // For runs, compare styling
        if (ElementType == "run")
        {
            return CompareRunContent(this, other);
        }

        return true;
    }

    /// <summary>
    /// Compute a similarity score between 0 and 1.
    /// Used for fuzzy matching when fingerprints don't match exactly.
    /// </summary>
    public double SimilarityTo(ElementSnapshot other)
    {
        // Different types = no similarity
        if (ElementType != other.ElementType)
            return 0.0;

        // Same fingerprint = identical
        if (Fingerprint == other.Fingerprint)
            return 1.0;

        // Compute text similarity using Levenshtein ratio
        var textSim = ComputeTextSimilarity(NormalizeText(Text), NormalizeText(other.Text));

        // For tables, also consider structure
        if (ElementType == "table")
        {
            var structureSim = CompareTableStructure(this, other);
            return (textSim + structureSim) / 2.0;
        }

        return textSim;
    }

    private static double ComputeTextSimilarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        var maxLen = Math.Max(a.Length, b.Length);
        var distance = LevenshteinDistance(a, b);
        return 1.0 - (double)distance / maxLen;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    private static bool CompareRunContent(ElementSnapshot a, ElementSnapshot b)
    {
        var aJson = a.JsonValue;
        var bJson = b.JsonValue;

        // Compare text
        var aText = aJson["text"]?.GetValue<string>() ?? "";
        var bText = bJson["text"]?.GetValue<string>() ?? "";
        if (aText != bText) return false;

        // Compare tab/break
        var aTab = aJson["tab"]?.GetValue<bool>() ?? false;
        var bTab = bJson["tab"]?.GetValue<bool>() ?? false;
        if (aTab != bTab) return false;

        // Compare style (if both have styles)
        var aStyle = aJson["style"]?.AsObject();
        var bStyle = bJson["style"]?.AsObject();

        if (aStyle is null && bStyle is null) return true;
        if (aStyle is null || bStyle is null) return false;

        return JsonObjectEquals(aStyle, bStyle);
    }

    private static bool CompareParagraphContent(ElementSnapshot a, ElementSnapshot b)
    {
        // Compare run count
        if (a.Children.Count != b.Children.Count)
            return false;

        // Compare each run
        for (int i = 0; i < a.Children.Count; i++)
        {
            if (!a.Children[i].ContentEquals(b.Children[i]))
                return false;
        }

        // Compare paragraph properties
        var aProps = a.JsonValue["properties"]?.AsObject();
        var bProps = b.JsonValue["properties"]?.AsObject();

        if (aProps is null && bProps is null) return true;
        if (aProps is null || bProps is null) return false;

        return JsonObjectEquals(aProps, bProps);
    }

    private static bool CompareTableContent(ElementSnapshot a, ElementSnapshot b)
    {
        // Compare row count
        if (a.Children.Count != b.Children.Count)
            return false;

        // Compare each row
        for (int i = 0; i < a.Children.Count; i++)
        {
            var rowA = a.Children[i];
            var rowB = b.Children[i];

            // Compare cell count
            if (rowA.Children.Count != rowB.Children.Count)
                return false;

            // Compare each cell's text (exact match)
            for (int j = 0; j < rowA.Children.Count; j++)
            {
                if (rowA.Children[j].Text != rowB.Children[j].Text)
                    return false;
            }
        }

        return true;
    }

    private static double CompareTableStructure(ElementSnapshot a, ElementSnapshot b)
    {
        if (a.Children.Count == 0 && b.Children.Count == 0) return 1.0;
        if (a.Children.Count == 0 || b.Children.Count == 0) return 0.0;

        var rowSim = 1.0 - Math.Abs(a.Children.Count - b.Children.Count) / (double)Math.Max(a.Children.Count, b.Children.Count);

        var aColCount = a.Children[0].Children.Count;
        var bColCount = b.Children[0].Children.Count;
        var colSim = aColCount == 0 && bColCount == 0 ? 1.0 :
            1.0 - Math.Abs(aColCount - bColCount) / (double)Math.Max(aColCount, bColCount);

        return (rowSim + colSim) / 2.0;
    }

    private static bool JsonObjectEquals(JsonObject a, JsonObject b)
    {
        if (a.Count != b.Count) return false;

        foreach (var kvp in a)
        {
            if (!b.TryGetPropertyValue(kvp.Key, out var bVal))
                return false;

            if (kvp.Value is null && bVal is null) continue;
            if (kvp.Value is null || bVal is null) return false;

            if (kvp.Value.ToJsonString() != bVal.ToJsonString())
                return false;
        }

        return true;
    }

    private static string GetElementTypeName(OpenXmlElement element) => element switch
    {
        Paragraph p when GetHeadingLevel(p) is not null => "heading",
        Paragraph => "paragraph",
        Table => "table",
        TableRow => "row",
        TableCell => "cell",
        Run => "run",
        Hyperlink => "hyperlink",
        BookmarkStart => "bookmark",
        _ => element.LocalName
    };

    private static int? GetHeadingLevel(OpenXmlElement element)
    {
        if (element is not Paragraph p) return null;

        var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (styleId is null) return null;

        if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(styleId.AsSpan(7), out var level))
        {
            return level;
        }

        // Also check for "Titre" (French) or numbered styles
        if (styleId.StartsWith("Titre", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(styleId.AsSpan(5), out level))
        {
            return level;
        }

        return null;
    }

    private static JsonObject ElementToJsonObject(OpenXmlElement element)
    {
        var result = new JsonObject
        {
            ["type"] = GetElementTypeName(element),
            ["text"] = element.InnerText
        };

        switch (element)
        {
            case Paragraph p:
                PopulateParagraphJson(p, result);
                break;
            case Table t:
                PopulateTableJson(t, result);
                break;
            case TableRow tr:
                PopulateRowJson(tr, result);
                break;
            case TableCell tc:
                PopulateCellJson(tc, result);
                break;
            case Run r:
                PopulateRunJson(r, result);
                break;
        }

        return result;
    }

    private static void PopulateParagraphJson(Paragraph p, JsonObject result)
    {
        if (GetHeadingLevel(p) is int level)
        {
            result["type"] = "heading";
            result["level"] = level;
        }

        // Paragraph properties
        if (p.ParagraphProperties is ParagraphProperties pp)
        {
            var props = new JsonObject();
            bool hasProps = false;

            if (pp.Justification?.Val is not null)
            {
                props["alignment"] = pp.Justification.Val.InnerText;
                hasProps = true;
            }

            var styleId = pp.ParagraphStyleId?.Val?.Value;
            if (styleId is not null && !styleId.StartsWith("Heading") && !styleId.StartsWith("Titre"))
            {
                props["style"] = styleId;
                hasProps = true;
            }

            if (hasProps)
                result["properties"] = props;
        }

        // Runs
        var runs = p.Elements<Run>().ToList();
        if (runs.Count > 0)
        {
            var runsArr = new JsonArray();
            foreach (var r in runs)
            {
                runsArr.Add((JsonNode?)RunToJsonObject(r));
            }
            result["runs"] = runsArr;
        }
    }

    private static void PopulateTableJson(Table t, JsonObject result)
    {
        var rows = t.Elements<TableRow>().ToList();
        result["row_count"] = rows.Count;

        if (rows.Count > 0)
        {
            result["col_count"] = rows[0].Elements<TableCell>().Count();

            // Capture all rows with their cells
            var rowsArr = new JsonArray();
            foreach (var row in rows)
            {
                var rowObj = new JsonObject();
                var cellsArr = new JsonArray();
                foreach (var cell in row.Elements<TableCell>())
                {
                    JsonNode? cellNode = System.Text.Json.Nodes.JsonValue.Create(cell.InnerText);
                    cellsArr.Add(cellNode);
                }
                rowObj["cells"] = cellsArr;
                rowsArr.Add((JsonNode?)rowObj);
            }
            result["rows"] = rowsArr;
        }

        // Table style
        var tblProps = t.GetFirstChild<TableProperties>();
        if (tblProps?.TableStyle?.Val?.Value is string style)
        {
            result["table_style"] = style;
        }
    }

    private static void PopulateRowJson(TableRow tr, JsonObject result)
    {
        var cells = tr.Elements<TableCell>().ToList();
        var cellsArr = new JsonArray();
        foreach (var c in cells)
        {
            JsonNode? cellNode = System.Text.Json.Nodes.JsonValue.Create(c.InnerText);
            cellsArr.Add(cellNode);
        }
        result["cells"] = cellsArr;

        if (tr.TableRowProperties?.GetFirstChild<TableHeader>() is not null)
        {
            result["is_header"] = true;
        }
    }

    private static void PopulateCellJson(TableCell tc, JsonObject result)
    {
        if (tc.TableCellProperties is TableCellProperties tcp)
        {
            if (tcp.Shading?.Fill?.Value is string fill)
                result["shading"] = fill;

            if (tcp.GridSpan?.Val?.Value is int span)
                result["col_span"] = span;
        }
    }

    private static void PopulateRunJson(Run r, JsonObject result)
    {
        // Check for tab
        if (r.GetFirstChild<TabChar>() is not null)
        {
            result["tab"] = true;
            result["text"] = "\t";
            return;
        }

        // Check for break
        if (r.GetFirstChild<Break>() is Break brk)
        {
            var breakType = "line";
            if (brk.Type?.Value == BreakValues.Page)
                breakType = "page";
            else if (brk.Type?.Value == BreakValues.Column)
                breakType = "column";
            result["break"] = breakType;
            result["text"] = "";
            return;
        }

        // Style properties
        if (r.RunProperties is RunProperties rp)
        {
            var style = new JsonObject();
            bool hasStyle = false;

            if (rp.Bold is not null) { style["bold"] = true; hasStyle = true; }
            if (rp.Italic is not null) { style["italic"] = true; hasStyle = true; }
            if (rp.Underline is not null) { style["underline"] = true; hasStyle = true; }
            if (rp.Strike is not null) { style["strike"] = true; hasStyle = true; }

            if (rp.FontSize?.Val?.Value is string fs && int.TryParse(fs, out var halfPts))
            {
                style["font_size"] = halfPts / 2;
                hasStyle = true;
            }

            if (rp.RunFonts?.Ascii?.Value is string font)
            {
                style["font_name"] = font;
                hasStyle = true;
            }

            if (rp.Color?.Val?.Value is string color)
            {
                style["color"] = color;
                hasStyle = true;
            }

            if (hasStyle)
                result["style"] = style;
        }
    }

    private static JsonObject RunToJsonObject(Run r)
    {
        var result = new JsonObject();

        // Check for tab
        if (r.GetFirstChild<TabChar>() is not null)
        {
            result["tab"] = true;
            return result;
        }

        // Check for break
        if (r.GetFirstChild<Break>() is Break brk)
        {
            var breakType = "line";
            if (brk.Type?.Value == BreakValues.Page)
                breakType = "page";
            else if (brk.Type?.Value == BreakValues.Column)
                breakType = "column";
            result["break"] = breakType;
            return result;
        }

        result["text"] = r.InnerText;

        // Style
        if (r.RunProperties is RunProperties rp)
        {
            var style = new JsonObject();
            bool hasStyle = false;

            if (rp.Bold is not null) { style["bold"] = true; hasStyle = true; }
            if (rp.Italic is not null) { style["italic"] = true; hasStyle = true; }
            if (rp.Underline is not null) { style["underline"] = true; hasStyle = true; }
            if (rp.Strike is not null) { style["strike"] = true; hasStyle = true; }

            if (rp.FontSize?.Val?.Value is string fs && int.TryParse(fs, out var halfPts))
            {
                style["font_size"] = halfPts / 2;
                hasStyle = true;
            }

            if (rp.RunFonts?.Ascii?.Value is string font)
            {
                style["font_name"] = font;
                hasStyle = true;
            }

            if (rp.Color?.Val?.Value is string color)
            {
                style["color"] = color;
                hasStyle = true;
            }

            if (hasStyle)
                result["style"] = style;
        }

        return result;
    }
}
