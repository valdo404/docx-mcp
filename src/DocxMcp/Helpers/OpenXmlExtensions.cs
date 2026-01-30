using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxMcp.Helpers;

/// <summary>
/// Convenience extension methods for the Open XML SDK.
/// </summary>
public static class OpenXmlExtensions
{
    /// <summary>
    /// Get the plain text content of an element (recursively).
    /// </summary>
    public static string GetText(this OpenXmlElement element)
    {
        return element.InnerText;
    }

    /// <summary>
    /// Get the style ID of a paragraph, if any.
    /// </summary>
    public static string? GetStyleId(this Paragraph paragraph)
    {
        return paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
    }

    /// <summary>
    /// Check if a paragraph is a heading (has a HeadingN style).
    /// </summary>
    public static bool IsHeading(this Paragraph paragraph)
    {
        var style = paragraph.GetStyleId();
        return style is not null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the heading level (1-9) or 0 if not a heading.
    /// </summary>
    public static int GetHeadingLevel(this Paragraph paragraph)
    {
        var style = paragraph.GetStyleId();
        if (style is null || !style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
            return 0;

        var levelStr = style["Heading".Length..];
        return int.TryParse(levelStr, out var level) ? level : 0;
    }

    /// <summary>
    /// Insert a child element at a specific index position.
    /// </summary>
    public static void InsertChildAt(this OpenXmlElement parent, OpenXmlElement child, int index)
    {
        var children = parent.ChildElements.ToList();

        if (index <= 0)
        {
            parent.PrependChild(child);
        }
        else if (index >= children.Count)
        {
            parent.AppendChild(child);
        }
        else
        {
            parent.InsertBefore(child, children[index]);
        }
    }

    /// <summary>
    /// Get table dimensions as (rows, cols).
    /// </summary>
    public static (int Rows, int Cols) GetTableDimensions(this Table table)
    {
        var rows = table.Elements<TableRow>().ToList();
        var maxCols = rows.Count > 0
            ? rows.Max(r => r.Elements<TableCell>().Count())
            : 0;
        return (rows.Count, maxCols);
    }

    /// <summary>
    /// Get a table cell's text by row and column index.
    /// </summary>
    public static string? GetCellText(this Table table, int row, int col)
    {
        var tableRow = table.Elements<TableRow>().ElementAtOrDefault(row);
        var cell = tableRow?.Elements<TableCell>().ElementAtOrDefault(col);
        return cell?.InnerText;
    }
}
