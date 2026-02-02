using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxMcp.Helpers;

/// <summary>
/// Merge-semantic helpers for applying style properties.
/// Only specified properties are changed; all others are preserved.
/// </summary>
public static class StyleHelper
{
    // --- Run (character) properties ---

    public static void MergeRunProperties(Run run, JsonElement style)
    {
        var props = run.RunProperties ?? new RunProperties();
        if (run.RunProperties is null)
            run.PrependChild(props);

        if (style.TryGetProperty("bold", out var bold))
        {
            if (bold.ValueKind == JsonValueKind.True)
                props.Bold = new Bold();
            else if (bold.ValueKind == JsonValueKind.False)
                props.Bold = null;
        }

        if (style.TryGetProperty("italic", out var italic))
        {
            if (italic.ValueKind == JsonValueKind.True)
                props.Italic = new Italic();
            else if (italic.ValueKind == JsonValueKind.False)
                props.Italic = null;
        }

        if (style.TryGetProperty("underline", out var underline))
        {
            if (underline.ValueKind == JsonValueKind.True)
                props.Underline = new Underline { Val = UnderlineValues.Single };
            else if (underline.ValueKind == JsonValueKind.False)
                props.Underline = null;
        }

        if (style.TryGetProperty("strike", out var strike))
        {
            if (strike.ValueKind == JsonValueKind.True)
                props.Strike = new Strike();
            else if (strike.ValueKind == JsonValueKind.False)
                props.Strike = null;
        }

        if (style.TryGetProperty("font_size", out var fontSize))
        {
            if (fontSize.ValueKind == JsonValueKind.Null)
                props.FontSize = null;
            else
                props.FontSize = new FontSize { Val = (fontSize.GetInt32() * 2).ToString() };
        }

        if (style.TryGetProperty("font_name", out var fontName))
        {
            if (fontName.ValueKind == JsonValueKind.Null)
                props.RunFonts = null;
            else
                props.RunFonts = new RunFonts { Ascii = fontName.GetString() };
        }

        if (style.TryGetProperty("color", out var color))
        {
            if (color.ValueKind == JsonValueKind.Null)
                props.Color = null;
            else
                props.Color = new Color { Val = color.GetString() };
        }

        if (style.TryGetProperty("highlight", out var highlight))
        {
            if (highlight.ValueKind == JsonValueKind.Null)
            {
                props.Highlight = null;
            }
            else
            {
                props.Highlight = new Highlight
                {
                    Val = highlight.GetString()?.ToLowerInvariant() switch
                    {
                        "yellow" => HighlightColorValues.Yellow,
                        "green" => HighlightColorValues.Green,
                        "cyan" => HighlightColorValues.Cyan,
                        "magenta" => HighlightColorValues.Magenta,
                        "blue" => HighlightColorValues.Blue,
                        "red" => HighlightColorValues.Red,
                        "dark_blue" => HighlightColorValues.DarkBlue,
                        "dark_cyan" => HighlightColorValues.DarkCyan,
                        "dark_green" => HighlightColorValues.DarkGreen,
                        "dark_magenta" => HighlightColorValues.DarkMagenta,
                        "dark_red" => HighlightColorValues.DarkRed,
                        "dark_yellow" => HighlightColorValues.DarkYellow,
                        "light_gray" => HighlightColorValues.LightGray,
                        "dark_gray" => HighlightColorValues.DarkGray,
                        "black" => HighlightColorValues.Black,
                        _ => HighlightColorValues.Yellow
                    }
                };
            }
        }

        if (style.TryGetProperty("vertical_align", out var vertAlign))
        {
            if (vertAlign.ValueKind == JsonValueKind.Null)
            {
                props.VerticalTextAlignment = null;
            }
            else
            {
                props.VerticalTextAlignment = new VerticalTextAlignment
                {
                    Val = vertAlign.GetString()?.ToLowerInvariant() switch
                    {
                        "superscript" => VerticalPositionValues.Superscript,
                        "subscript" => VerticalPositionValues.Subscript,
                        _ => VerticalPositionValues.Baseline
                    }
                };
            }
        }
    }

    // --- Paragraph properties ---

    public static void MergeParagraphProperties(Paragraph paragraph, JsonElement style)
    {
        var props = paragraph.ParagraphProperties ?? new ParagraphProperties();
        if (paragraph.ParagraphProperties is null)
            paragraph.PrependChild(props);

        if (style.TryGetProperty("alignment", out var align))
        {
            if (align.ValueKind == JsonValueKind.Null)
            {
                props.Justification = null;
            }
            else
            {
                props.Justification = new Justification
                {
                    Val = align.GetString()?.ToLowerInvariant() switch
                    {
                        "left" => JustificationValues.Left,
                        "center" => JustificationValues.Center,
                        "right" => JustificationValues.Right,
                        "justify" => JustificationValues.Both,
                        _ => JustificationValues.Left
                    }
                };
            }
        }

        if (style.TryGetProperty("style", out var styleProp))
        {
            if (styleProp.ValueKind == JsonValueKind.Null)
                props.ParagraphStyleId = null;
            else
                props.ParagraphStyleId = new ParagraphStyleId { Val = styleProp.GetString() };
        }

        // Compound property: spacing — merge sub-fields independently
        MergeSpacing(props, style);

        // Compound property: indentation — merge sub-fields independently
        MergeIndentation(props, style);

        if (style.TryGetProperty("shading", out var shading))
        {
            if (shading.ValueKind == JsonValueKind.Null)
            {
                props.Shading = null;
            }
            else
            {
                props.Shading = new Shading
                {
                    Fill = shading.GetString(),
                    Val = ShadingPatternValues.Clear
                };
            }
        }
    }

    private static void MergeSpacing(ParagraphProperties props, JsonElement style)
    {
        bool hasSpacingProp =
            style.TryGetProperty("spacing_before", out _) ||
            style.TryGetProperty("spacing_after", out _) ||
            style.TryGetProperty("line_spacing", out _);

        if (!hasSpacingProp) return;

        var spacing = props.SpacingBetweenLines ?? new SpacingBetweenLines();

        if (style.TryGetProperty("spacing_before", out var sb))
        {
            if (sb.ValueKind == JsonValueKind.Null)
                spacing.Before = null;
            else
                spacing.Before = sb.GetInt32().ToString();
        }

        if (style.TryGetProperty("spacing_after", out var sa))
        {
            if (sa.ValueKind == JsonValueKind.Null)
                spacing.After = null;
            else
                spacing.After = sa.GetInt32().ToString();
        }

        if (style.TryGetProperty("line_spacing", out var ls))
        {
            if (ls.ValueKind == JsonValueKind.Null)
                spacing.Line = null;
            else
                spacing.Line = ls.GetInt32().ToString();
        }

        // Clean up: if all sub-fields are null, remove the element
        if (spacing.Before is null && spacing.After is null && spacing.Line is null)
        {
            props.SpacingBetweenLines = null;
        }
        else
        {
            props.SpacingBetweenLines = spacing;
        }
    }

    private static void MergeIndentation(ParagraphProperties props, JsonElement style)
    {
        bool hasIndentProp =
            style.TryGetProperty("indent_left", out _) ||
            style.TryGetProperty("indent_right", out _) ||
            style.TryGetProperty("indent_first_line", out _) ||
            style.TryGetProperty("indent_hanging", out _);

        if (!hasIndentProp) return;

        var indent = props.Indentation ?? new Indentation();

        if (style.TryGetProperty("indent_left", out var il))
        {
            if (il.ValueKind == JsonValueKind.Null)
                indent.Left = null;
            else
                indent.Left = il.GetInt32().ToString();
        }

        if (style.TryGetProperty("indent_right", out var ir))
        {
            if (ir.ValueKind == JsonValueKind.Null)
                indent.Right = null;
            else
                indent.Right = ir.GetInt32().ToString();
        }

        if (style.TryGetProperty("indent_first_line", out var ifl))
        {
            if (ifl.ValueKind == JsonValueKind.Null)
                indent.FirstLine = null;
            else
                indent.FirstLine = ifl.GetInt32().ToString();
        }

        if (style.TryGetProperty("indent_hanging", out var ih))
        {
            if (ih.ValueKind == JsonValueKind.Null)
                indent.Hanging = null;
            else
                indent.Hanging = ih.GetInt32().ToString();
        }

        // Clean up: if all sub-fields are null, remove the element
        if (indent.Left is null && indent.Right is null &&
            indent.FirstLine is null && indent.Hanging is null)
        {
            props.Indentation = null;
        }
        else
        {
            props.Indentation = indent;
        }
    }

    // --- Table properties ---

    public static void MergeTableProperties(Table table, JsonElement style)
    {
        var props = table.GetFirstChild<TableProperties>() ?? new TableProperties();
        if (table.GetFirstChild<TableProperties>() is null)
            table.PrependChild(props);

        if (style.TryGetProperty("border_style", out var bs))
        {
            if (bs.ValueKind == JsonValueKind.Null)
            {
                props.TableBorders = null;
            }
            else
            {
                var borderStyle = bs.GetString() ?? "single";
                if (borderStyle == "none")
                {
                    props.TableBorders = null;
                }
                else
                {
                    var borderValue = ElementFactory.ParseBorderValue(borderStyle);
                    var borderSize = style.TryGetProperty("border_size", out var bsz)
                        ? (uint)bsz.GetInt32()
                        : props.TableBorders?.TopBorder?.Size?.Value ?? 4u;

                    props.TableBorders = new TableBorders(
                        new TopBorder { Val = borderValue, Size = borderSize },
                        new BottomBorder { Val = borderValue, Size = borderSize },
                        new LeftBorder { Val = borderValue, Size = borderSize },
                        new RightBorder { Val = borderValue, Size = borderSize },
                        new InsideHorizontalBorder { Val = borderValue, Size = borderSize },
                        new InsideVerticalBorder { Val = borderValue, Size = borderSize }
                    );
                }
            }
        }
        else if (style.TryGetProperty("border_size", out var bszOnly))
        {
            // border_size without border_style: update size on existing borders
            if (props.TableBorders is not null)
            {
                var size = (uint)bszOnly.GetInt32();
                foreach (var border in props.TableBorders.ChildElements.OfType<BorderType>())
                {
                    border.Size = size;
                }
            }
        }

        if (style.TryGetProperty("width", out var width))
        {
            if (width.ValueKind == JsonValueKind.Null)
            {
                props.TableWidth = null;
            }
            else
            {
                var widthType = style.TryGetProperty("width_type", out var wt)
                    ? wt.GetString()?.ToLowerInvariant() switch
                    {
                        "pct" => TableWidthUnitValues.Pct,
                        "dxa" => TableWidthUnitValues.Dxa,
                        "auto" => TableWidthUnitValues.Auto,
                        _ => TableWidthUnitValues.Dxa
                    }
                    : props.TableWidth?.Type?.Value ?? TableWidthUnitValues.Dxa;

                props.TableWidth = new TableWidth
                {
                    Width = width.GetInt32().ToString(),
                    Type = widthType
                };
            }
        }

        if (style.TryGetProperty("table_style", out var ts))
        {
            if (ts.ValueKind == JsonValueKind.Null)
                props.TableStyle = null;
            else
                props.TableStyle = new TableStyle { Val = ts.GetString() };
        }

        if (style.TryGetProperty("table_alignment", out var ta))
        {
            if (ta.ValueKind == JsonValueKind.Null)
            {
                props.TableJustification = null;
            }
            else
            {
                props.TableJustification = new TableJustification
                {
                    Val = ta.GetString()?.ToLowerInvariant() switch
                    {
                        "left" => TableRowAlignmentValues.Left,
                        "center" => TableRowAlignmentValues.Center,
                        "right" => TableRowAlignmentValues.Right,
                        _ => TableRowAlignmentValues.Left
                    }
                };
            }
        }
    }

    // --- Table cell properties ---

    public static void MergeTableCellProperties(TableCell cell, JsonElement style)
    {
        var props = cell.GetFirstChild<TableCellProperties>() ?? new TableCellProperties();
        if (cell.GetFirstChild<TableCellProperties>() is null)
            cell.PrependChild(props);

        if (style.TryGetProperty("shading", out var shading))
        {
            if (shading.ValueKind == JsonValueKind.Null)
            {
                props.Shading = null;
            }
            else
            {
                props.Shading = new Shading
                {
                    Fill = shading.GetString(),
                    Val = ShadingPatternValues.Clear
                };
            }
        }

        if (style.TryGetProperty("vertical_align", out var va))
        {
            if (va.ValueKind == JsonValueKind.Null)
            {
                props.TableCellVerticalAlignment = null;
            }
            else
            {
                props.TableCellVerticalAlignment = new TableCellVerticalAlignment
                {
                    Val = va.GetString()?.ToLowerInvariant() switch
                    {
                        "top" => TableVerticalAlignmentValues.Top,
                        "center" => TableVerticalAlignmentValues.Center,
                        "bottom" => TableVerticalAlignmentValues.Bottom,
                        _ => TableVerticalAlignmentValues.Top
                    }
                };
            }
        }

        if (style.TryGetProperty("width", out var width))
        {
            if (width.ValueKind == JsonValueKind.Null)
            {
                props.TableCellWidth = null;
            }
            else
            {
                props.TableCellWidth = new TableCellWidth
                {
                    Width = width.GetInt32().ToString(),
                    Type = TableWidthUnitValues.Dxa
                };
            }
        }

        if (style.TryGetProperty("borders", out var borders))
        {
            if (borders.ValueKind == JsonValueKind.Null)
            {
                props.TableCellBorders = null;
            }
            else
            {
                var cb = props.TableCellBorders ?? new TableCellBorders();

                if (borders.TryGetProperty("top", out var top))
                    cb.TopBorder = new TopBorder { Val = ElementFactory.ParseBorderValue(top.GetString()), Size = 4 };
                if (borders.TryGetProperty("bottom", out var bottom))
                    cb.BottomBorder = new BottomBorder { Val = ElementFactory.ParseBorderValue(bottom.GetString()), Size = 4 };
                if (borders.TryGetProperty("left", out var left))
                    cb.LeftBorder = new LeftBorder { Val = ElementFactory.ParseBorderValue(left.GetString()), Size = 4 };
                if (borders.TryGetProperty("right", out var right))
                    cb.RightBorder = new RightBorder { Val = ElementFactory.ParseBorderValue(right.GetString()), Size = 4 };

                props.TableCellBorders = cb;
            }
        }
    }

    // --- Table row properties ---

    public static void MergeTableRowProperties(TableRow row, JsonElement style)
    {
        var props = row.TableRowProperties ?? new TableRowProperties();
        if (row.TableRowProperties is null)
            row.PrependChild(props);

        if (style.TryGetProperty("height", out var height))
        {
            if (height.ValueKind == JsonValueKind.Null)
            {
                var existing = props.GetFirstChild<TableRowHeight>();
                existing?.Remove();
            }
            else
            {
                var existing = props.GetFirstChild<TableRowHeight>();
                if (existing is not null)
                    existing.Val = (uint)height.GetInt32();
                else
                    props.AppendChild(new TableRowHeight { Val = (uint)height.GetInt32() });
            }
        }

        if (style.TryGetProperty("is_header", out var isHeader))
        {
            var existing = props.GetFirstChild<TableHeader>();
            if (isHeader.ValueKind == JsonValueKind.True)
            {
                if (existing is null)
                    props.AppendChild(new TableHeader());
            }
            else if (isHeader.ValueKind == JsonValueKind.False)
            {
                existing?.Remove();
            }
        }
    }

    // --- Collection helpers ---

    public static List<Run> CollectRuns(OpenXmlElement element)
    {
        return element.Descendants<Run>().ToList();
    }

    public static List<Paragraph> CollectParagraphs(OpenXmlElement element)
    {
        if (element is Paragraph p)
            return [p];
        return element.Descendants<Paragraph>().ToList();
    }

    public static List<Table> CollectTables(OpenXmlElement element)
    {
        if (element is Table t)
            return [t];
        return element.Descendants<Table>().ToList();
    }
}
