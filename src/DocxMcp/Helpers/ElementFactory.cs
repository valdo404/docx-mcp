using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxMcp.Helpers;

/// <summary>
/// Creates Open XML elements from JSON patch values.
/// </summary>
public static class ElementFactory
{
    /// <summary>
    /// Create an OpenXmlElement from a JSON value object.
    /// The "type" field determines what element to create.
    /// </summary>
    public static OpenXmlElement CreateFromJson(JsonElement value, MainDocumentPart mainPart)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Patch value must be a JSON object.");

        var type = value.GetProperty("type").GetString()
            ?? throw new ArgumentException("Patch value must have a 'type' field.");

        return type.ToLowerInvariant() switch
        {
            "paragraph" => CreateParagraph(value),
            "heading" => CreateHeading(value),
            "table" => CreateTable(value),
            "row" => CreateRowFromJson(value),
            "cell" => CreateRichTableCell(value, false),
            "image" => CreateImage(value, mainPart),
            "hyperlink" => CreateHyperlink(value, mainPart),
            "page_break" => CreatePageBreak(),
            "section_break" => CreateSectionBreak(value),
            "list" => CreateList(value),
            _ => throw new ArgumentException($"Unknown element type: '{type}'")
        };
    }

    /// <summary>
    /// Apply style properties from a JSON object to paragraph/run properties.
    /// </summary>
    public static ParagraphProperties CreateParagraphProperties(JsonElement style)
    {
        var props = new ParagraphProperties();

        if (style.TryGetProperty("alignment", out var align))
        {
            var justification = align.GetString()?.ToLowerInvariant() switch
            {
                "left" => JustificationValues.Left,
                "center" => JustificationValues.Center,
                "right" => JustificationValues.Right,
                "justify" => JustificationValues.Both,
                _ => JustificationValues.Left
            };
            props.Justification = new Justification { Val = justification };
        }

        if (style.TryGetProperty("style", out var styleProp))
        {
            props.ParagraphStyleId = new ParagraphStyleId { Val = styleProp.GetString() };
        }

        // Paragraph spacing
        if (style.TryGetProperty("spacing_before", out var spaceBefore) ||
            style.TryGetProperty("spacing_after", out var spaceAfter) ||
            style.TryGetProperty("line_spacing", out var lineSpacing))
        {
            var spacing = new SpacingBetweenLines();
            if (style.TryGetProperty("spacing_before", out spaceBefore))
                spacing.Before = spaceBefore.GetInt32().ToString();
            if (style.TryGetProperty("spacing_after", out spaceAfter))
                spacing.After = spaceAfter.GetInt32().ToString();
            if (style.TryGetProperty("line_spacing", out lineSpacing))
                spacing.Line = lineSpacing.GetInt32().ToString();
            props.SpacingBetweenLines = spacing;
        }

        // Indentation
        if (style.TryGetProperty("indent_left", out var indentLeft) ||
            style.TryGetProperty("indent_right", out var indentRight) ||
            style.TryGetProperty("indent_first_line", out var indentFirst) ||
            style.TryGetProperty("indent_hanging", out var indentHanging))
        {
            var indent = new Indentation();
            if (style.TryGetProperty("indent_left", out indentLeft))
                indent.Left = indentLeft.GetInt32().ToString();
            if (style.TryGetProperty("indent_right", out indentRight))
                indent.Right = indentRight.GetInt32().ToString();
            if (style.TryGetProperty("indent_first_line", out indentFirst))
                indent.FirstLine = indentFirst.GetInt32().ToString();
            if (style.TryGetProperty("indent_hanging", out indentHanging))
                indent.Hanging = indentHanging.GetInt32().ToString();
            props.Indentation = indent;
        }

        // Tab stops
        if (style.TryGetProperty("tabs", out var tabs) && tabs.ValueKind == JsonValueKind.Array)
        {
            var tabsElem = new Tabs();
            foreach (var tab in tabs.EnumerateArray())
            {
                var tabStop = new TabStop();
                if (tab.TryGetProperty("position", out var pos))
                    tabStop.Position = pos.GetInt32();
                if (tab.TryGetProperty("alignment", out var tabAlign))
                {
                    tabStop.Val = tabAlign.GetString()?.ToLowerInvariant() switch
                    {
                        "left" => TabStopValues.Left,
                        "center" => TabStopValues.Center,
                        "right" => TabStopValues.Right,
                        "decimal" => TabStopValues.Decimal,
                        "bar" => TabStopValues.Bar,
                        "clear" => TabStopValues.Clear,
                        _ => TabStopValues.Left
                    };
                }
                else
                {
                    tabStop.Val = TabStopValues.Left;
                }
                if (tab.TryGetProperty("leader", out var leader))
                {
                    tabStop.Leader = leader.GetString()?.ToLowerInvariant() switch
                    {
                        "dot" => TabStopLeaderCharValues.Dot,
                        "hyphen" => TabStopLeaderCharValues.Hyphen,
                        "underscore" => TabStopLeaderCharValues.Underscore,
                        "heavy" => TabStopLeaderCharValues.Heavy,
                        "middledot" => TabStopLeaderCharValues.MiddleDot,
                        _ => TabStopLeaderCharValues.None
                    };
                }
                tabsElem.AppendChild(tabStop);
            }
            props.Tabs = tabsElem;
        }

        return props;
    }

    public static RunProperties CreateRunProperties(JsonElement style)
    {
        var props = new RunProperties();

        if (style.TryGetProperty("bold", out var bold) && bold.GetBoolean())
            props.Bold = new Bold();

        if (style.TryGetProperty("italic", out var italic) && italic.GetBoolean())
            props.Italic = new Italic();

        if (style.TryGetProperty("underline", out var underline) && underline.GetBoolean())
            props.Underline = new Underline { Val = UnderlineValues.Single };

        if (style.TryGetProperty("strike", out var strike) && strike.GetBoolean())
            props.Strike = new Strike();

        if (style.TryGetProperty("font_size", out var fontSize))
        {
            // Font size in half-points
            var halfPoints = (fontSize.GetInt32() * 2).ToString();
            props.FontSize = new FontSize { Val = halfPoints };
        }

        if (style.TryGetProperty("font_name", out var fontName))
        {
            props.RunFonts = new RunFonts { Ascii = fontName.GetString() };
        }

        if (style.TryGetProperty("color", out var color))
        {
            props.Color = new Color { Val = color.GetString() };
        }

        if (style.TryGetProperty("highlight", out var highlight))
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

        if (style.TryGetProperty("vertical_align", out var vertAlign))
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

        return props;
    }

    /// <summary>
    /// Create a Run element from a JSON run descriptor.
    /// Supports: text, tab, style properties.
    /// </summary>
    public static Run CreateRun(JsonElement runJson)
    {
        var run = new Run();

        // Apply run-level style
        if (runJson.TryGetProperty("style", out var style))
        {
            run.RunProperties = CreateRunProperties(style);
        }

        // Determine content type
        if (runJson.TryGetProperty("tab", out var tab) && tab.GetBoolean())
        {
            // This run is a tab character
            run.AppendChild(new TabChar());
        }
        else if (runJson.TryGetProperty("break", out var brk))
        {
            var breakType = brk.GetString()?.ToLowerInvariant() switch
            {
                "line" => BreakValues.TextWrapping,
                "page" => BreakValues.Page,
                "column" => BreakValues.Column,
                _ => BreakValues.TextWrapping
            };
            run.AppendChild(new Break { Type = breakType });
        }
        else
        {
            // Regular text run
            var text = runJson.TryGetProperty("text", out var txt)
                ? txt.GetString() ?? ""
                : "";
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        }

        ElementIdManager.AssignId(run);
        return run;
    }

    /// <summary>
    /// Populate a paragraph with runs from a JSON runs array, or fall back to flat text.
    /// </summary>
    private static void PopulateRuns(Paragraph paragraph, JsonElement value)
    {
        // If runs array is provided, use run-level write support
        if (value.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var runJson in runs.EnumerateArray())
            {
                paragraph.AppendChild(CreateRun(runJson));
            }
            return;
        }

        // Fall back to flat text with optional style
        if (value.TryGetProperty("text", out var text))
        {
            var run = new Run();

            if (value.TryGetProperty("style", out var runStyle))
            {
                run.RunProperties = CreateRunProperties(runStyle);
            }

            run.AppendChild(new Text(text.GetString() ?? "") { Space = SpaceProcessingModeValues.Preserve });
            ElementIdManager.AssignId(run);
            paragraph.AppendChild(run);
        }
    }

    private static Paragraph CreateParagraph(JsonElement value)
    {
        var paragraph = new Paragraph();

        // Apply paragraph-level properties
        if (value.TryGetProperty("properties", out var props))
        {
            paragraph.ParagraphProperties = CreateParagraphProperties(props);
        }
        else if (value.TryGetProperty("style", out var style))
        {
            if (value.TryGetProperty("runs", out _))
            {
                // When runs are present, "style" is a paragraph style name (e.g. "Heading1")
                paragraph.ParagraphProperties ??= new ParagraphProperties();
                paragraph.ParagraphProperties.ParagraphStyleId = new ParagraphStyleId { Val = style.GetString() };
            }
            else
            {
                // Legacy: when no runs array, "style" applies to both paragraph and run
                paragraph.ParagraphProperties = CreateParagraphProperties(style);
            }
        }

        PopulateRuns(paragraph, value);

        ElementIdManager.AssignId(paragraph);
        return paragraph;
    }

    private static Paragraph CreateHeading(JsonElement value)
    {
        var level = value.TryGetProperty("level", out var lvl) ? lvl.GetInt32() : 1;

        var paragraph = new Paragraph();

        // Start with heading style
        var paragraphProps = new ParagraphProperties
        {
            ParagraphStyleId = new ParagraphStyleId { Val = $"Heading{level}" }
        };

        // Merge additional paragraph properties if provided
        if (value.TryGetProperty("properties", out var props))
        {
            var extraProps = CreateParagraphProperties(props);

            // Copy over any properties that were explicitly set
            if (extraProps.Justification is not null)
                paragraphProps.Justification = (Justification)extraProps.Justification.CloneNode(true);
            if (extraProps.SpacingBetweenLines is not null)
                paragraphProps.SpacingBetweenLines = (SpacingBetweenLines)extraProps.SpacingBetweenLines.CloneNode(true);
            if (extraProps.Indentation is not null)
                paragraphProps.Indentation = (Indentation)extraProps.Indentation.CloneNode(true);
            if (extraProps.Tabs is not null)
                paragraphProps.Tabs = (Tabs)extraProps.Tabs.CloneNode(true);
        }

        paragraph.ParagraphProperties = paragraphProps;

        PopulateRuns(paragraph, value);

        ElementIdManager.AssignId(paragraph);
        return paragraph;
    }

    private static Table CreateTable(JsonElement value)
    {
        var table = new Table();

        // Table properties with borders
        var tblProps = CreateTableProperties(value);
        table.AppendChild(tblProps);

        // Table grid (column definitions)
        if (value.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
        {
            var grid = new TableGrid();
            foreach (var col in columns.EnumerateArray())
            {
                var gridCol = new GridColumn();
                if (col.TryGetProperty("width", out var w))
                    gridCol.Width = w.GetInt32().ToString();
                grid.AppendChild(gridCol);
            }
            table.AppendChild(grid);
        }

        // Headers row
        if (value.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
        {
            var headerRow = CreateTableRow(headers, isHeader: true);
            table.AppendChild(headerRow);
        }

        // Data rows
        if (value.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Array)
                {
                    // Simple string array row
                    var tableRow = CreateTableRow(row, isHeader: false);
                    table.AppendChild(tableRow);
                }
                else if (row.ValueKind == JsonValueKind.Object)
                {
                    // Rich row object with cells array
                    var tableRow = CreateRichTableRow(row);
                    table.AppendChild(tableRow);
                }
            }
        }

        ElementIdManager.AssignId(table);
        return table;
    }

    /// <summary>
    /// Create table properties from a JSON value.
    /// </summary>
    public static TableProperties CreateTableProperties(JsonElement value)
    {
        var tblProps = new TableProperties();
        var borderStyle = value.TryGetProperty("border_style", out var bs)
            ? bs.GetString() ?? "single"
            : "single";

        if (borderStyle != "none")
        {
            var borderValue = ParseBorderValue(borderStyle);
            var borderSize = value.TryGetProperty("border_size", out var bsz)
                ? (uint)bsz.GetInt32()
                : 4u;

            tblProps.TableBorders = new TableBorders(
                new TopBorder { Val = borderValue, Size = borderSize },
                new BottomBorder { Val = borderValue, Size = borderSize },
                new LeftBorder { Val = borderValue, Size = borderSize },
                new RightBorder { Val = borderValue, Size = borderSize },
                new InsideHorizontalBorder { Val = borderValue, Size = borderSize },
                new InsideVerticalBorder { Val = borderValue, Size = borderSize }
            );
        }

        // Table width
        if (value.TryGetProperty("width", out var width))
        {
            var widthType = value.TryGetProperty("width_type", out var wt)
                ? wt.GetString()?.ToLowerInvariant() switch
                {
                    "pct" => TableWidthUnitValues.Pct,
                    "dxa" => TableWidthUnitValues.Dxa,
                    "auto" => TableWidthUnitValues.Auto,
                    _ => TableWidthUnitValues.Dxa
                }
                : TableWidthUnitValues.Dxa;

            tblProps.TableWidth = new TableWidth
            {
                Width = width.GetInt32().ToString(),
                Type = widthType
            };
        }

        // Table style
        if (value.TryGetProperty("table_style", out var tableStyle))
        {
            tblProps.TableStyle = new TableStyle { Val = tableStyle.GetString() };
        }

        // Table alignment
        if (value.TryGetProperty("table_alignment", out var tblAlign))
        {
            tblProps.TableJustification = new TableJustification
            {
                Val = tblAlign.GetString()?.ToLowerInvariant() switch
                {
                    "left" => TableRowAlignmentValues.Left,
                    "center" => TableRowAlignmentValues.Center,
                    "right" => TableRowAlignmentValues.Right,
                    _ => TableRowAlignmentValues.Left
                }
            };
        }

        return tblProps;
    }

    /// <summary>
    /// Create a table row from a JSON array of cell values (strings or rich objects).
    /// </summary>
    private static TableRow CreateTableRow(JsonElement cells, bool isHeader)
    {
        var tableRow = new TableRow();

        if (isHeader)
        {
            // Mark as header row (repeats on page breaks)
            tableRow.TableRowProperties = new TableRowProperties(
                new TableHeader());
        }

        foreach (var cell in cells.EnumerateArray())
        {
            if (cell.ValueKind == JsonValueKind.Object)
            {
                tableRow.AppendChild(CreateRichTableCell(cell, isHeader));
            }
            else
            {
                // Simple string cell
                var tc = new TableCell();
                var p = new Paragraph();
                var r = new Run();
                if (isHeader)
                    r.RunProperties = new RunProperties { Bold = new Bold() };
                r.AppendChild(new Text(cell.GetString() ?? cell.ToString())
                    { Space = SpaceProcessingModeValues.Preserve });
                ElementIdManager.AssignId(r);
                p.AppendChild(r);
                ElementIdManager.AssignId(p);
                tc.AppendChild(p);
                ElementIdManager.AssignId(tc);
                tableRow.AppendChild(tc);
            }
        }

        ElementIdManager.AssignId(tableRow);
        return tableRow;
    }

    /// <summary>
    /// Create a table row from a JSON value (for use as top-level type in patches).
    /// Accepts: {"type": "row", "cells": [...], "height": N, "is_header": bool}
    /// </summary>
    private static TableRow CreateRowFromJson(JsonElement value)
    {
        return CreateRichTableRow(value);
    }

    /// <summary>
    /// Create a table row from a rich JSON object with "cells" array and optional row properties.
    /// </summary>
    private static TableRow CreateRichTableRow(JsonElement rowJson)
    {
        var tableRow = new TableRow();

        // Row properties
        if (rowJson.TryGetProperty("height", out var height))
        {
            tableRow.TableRowProperties = new TableRowProperties(
                new TableRowHeight { Val = (uint)height.GetInt32() });
        }

        if (rowJson.TryGetProperty("is_header", out var isH) && isH.GetBoolean())
        {
            var rowProps = tableRow.TableRowProperties ?? new TableRowProperties();
            rowProps.AppendChild(new TableHeader());
            tableRow.TableRowProperties = rowProps;
        }

        if (rowJson.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
        {
            foreach (var cell in cells.EnumerateArray())
            {
                if (cell.ValueKind == JsonValueKind.Object)
                {
                    tableRow.AppendChild(CreateRichTableCell(cell, false));
                }
                else
                {
                    var tc = new TableCell();
                    var p = new Paragraph();
                    var r = new Run();
                    r.AppendChild(new Text(cell.GetString() ?? cell.ToString())
                        { Space = SpaceProcessingModeValues.Preserve });
                    ElementIdManager.AssignId(r);
                    p.AppendChild(r);
                    ElementIdManager.AssignId(p);
                    tc.AppendChild(p);
                    ElementIdManager.AssignId(tc);
                    tableRow.AppendChild(tc);
                }
            }
        }

        ElementIdManager.AssignId(tableRow);
        return tableRow;
    }

    /// <summary>
    /// Create a rich table cell from a JSON object with text, style, runs, etc.
    /// </summary>
    public static TableCell CreateRichTableCell(JsonElement cellJson, bool isHeader)
    {
        var tc = new TableCell();

        // Cell properties
        var tcProps = new TableCellProperties();
        bool hasProps = false;

        if (cellJson.TryGetProperty("width", out var w))
        {
            tcProps.TableCellWidth = new TableCellWidth
            {
                Width = w.GetInt32().ToString(),
                Type = TableWidthUnitValues.Dxa
            };
            hasProps = true;
        }

        if (cellJson.TryGetProperty("vertical_align", out var vAlign))
        {
            tcProps.TableCellVerticalAlignment = new TableCellVerticalAlignment
            {
                Val = vAlign.GetString()?.ToLowerInvariant() switch
                {
                    "top" => TableVerticalAlignmentValues.Top,
                    "center" => TableVerticalAlignmentValues.Center,
                    "bottom" => TableVerticalAlignmentValues.Bottom,
                    _ => TableVerticalAlignmentValues.Top
                }
            };
            hasProps = true;
        }

        if (cellJson.TryGetProperty("shading", out var shading))
        {
            tcProps.Shading = new Shading
            {
                Fill = shading.GetString(),
                Val = ShadingPatternValues.Clear
            };
            hasProps = true;
        }

        if (cellJson.TryGetProperty("col_span", out var colSpan))
        {
            tcProps.GridSpan = new GridSpan { Val = colSpan.GetInt32() };
            hasProps = true;
        }

        if (cellJson.TryGetProperty("row_span", out var rowSpan))
        {
            var spanVal = rowSpan.GetString()?.ToLowerInvariant();
            if (spanVal == "restart")
                tcProps.VerticalMerge = new VerticalMerge { Val = MergedCellValues.Restart };
            else if (spanVal == "continue")
                tcProps.VerticalMerge = new VerticalMerge();
            hasProps = true;
        }

        // Cell borders
        if (cellJson.TryGetProperty("borders", out var borders))
        {
            tcProps.TableCellBorders = CreateCellBorders(borders);
            hasProps = true;
        }

        if (hasProps)
            tc.AppendChild(tcProps);

        // Cell content: supports runs array, paragraphs array, or flat text
        if (cellJson.TryGetProperty("paragraphs", out var paragraphs) && paragraphs.ValueKind == JsonValueKind.Array)
        {
            foreach (var pJson in paragraphs.EnumerateArray())
            {
                if (pJson.ValueKind == JsonValueKind.Object)
                {
                    var pType = pJson.TryGetProperty("type", out var t) ? t.GetString() : "paragraph";
                    if (pType == "heading")
                        tc.AppendChild(CreateHeading(pJson));
                    else
                        tc.AppendChild(CreateParagraph(pJson));
                }
                else
                {
                    var p = new Paragraph();
                    var r = new Run();
                    r.AppendChild(new Text(pJson.GetString() ?? "") { Space = SpaceProcessingModeValues.Preserve });
                    p.AppendChild(r);
                    tc.AppendChild(p);
                }
            }
        }
        else if (cellJson.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            // Runs in a single paragraph
            var p = new Paragraph();
            foreach (var runJson in runs.EnumerateArray())
            {
                p.AppendChild(CreateRun(runJson));
            }
            tc.AppendChild(p);
        }
        else
        {
            var p = new Paragraph();
            var r = new Run();
            if (isHeader)
                r.RunProperties = new RunProperties { Bold = new Bold() };

            if (cellJson.TryGetProperty("style", out var cellStyle))
                r.RunProperties = CreateRunProperties(cellStyle);

            var text = cellJson.TryGetProperty("text", out var txt)
                ? txt.GetString() ?? ""
                : "";
            r.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            p.AppendChild(r);
            tc.AppendChild(p);
        }

        ElementIdManager.AssignId(tc);
        return tc;
    }

    /// <summary>
    /// Create cell border properties from JSON.
    /// </summary>
    private static TableCellBorders CreateCellBorders(JsonElement borders)
    {
        var cb = new TableCellBorders();

        if (borders.TryGetProperty("top", out var top))
            cb.TopBorder = new TopBorder { Val = ParseBorderValue(top.GetString()), Size = 4 };
        if (borders.TryGetProperty("bottom", out var bottom))
            cb.BottomBorder = new BottomBorder { Val = ParseBorderValue(bottom.GetString()), Size = 4 };
        if (borders.TryGetProperty("left", out var left))
            cb.LeftBorder = new LeftBorder { Val = ParseBorderValue(left.GetString()), Size = 4 };
        if (borders.TryGetProperty("right", out var right))
            cb.RightBorder = new RightBorder { Val = ParseBorderValue(right.GetString()), Size = 4 };

        return cb;
    }

    internal static BorderValues ParseBorderValue(string? style)
    {
        return style?.ToLowerInvariant() switch
        {
            "single" => BorderValues.Single,
            "double" => BorderValues.Double,
            "dashed" => BorderValues.Dashed,
            "dotted" => BorderValues.Dotted,
            "none" or "nil" => BorderValues.Nil,
            "thick" => BorderValues.Thick,
            "thin_thick_small_gap" => BorderValues.ThinThickSmallGap,
            _ => BorderValues.Single
        };
    }

    private static Paragraph CreateImage(JsonElement value, MainDocumentPart mainPart)
    {
        var imagePath = value.GetProperty("path").GetString()
            ?? throw new ArgumentException("Image must have a 'path' field.");
        var width = value.TryGetProperty("width", out var w) ? w.GetInt64() : 200;
        var height = value.TryGetProperty("height", out var h) ? h.GetInt64() : 150;
        var alt = value.TryGetProperty("alt", out var a) ? a.GetString() ?? "" : "";

        if (!File.Exists(imagePath))
            throw new FileNotFoundException($"Image file not found: {imagePath}");

        // Determine image type
        var ext = Path.GetExtension(imagePath).ToLowerInvariant();
        var imageType = ext switch
        {
            ".png" => ImagePartType.Png,
            ".jpg" or ".jpeg" => ImagePartType.Jpeg,
            ".gif" => ImagePartType.Gif,
            ".bmp" => ImagePartType.Bmp,
            _ => throw new ArgumentException($"Unsupported image format: {ext}")
        };

        // Add image part
        var imagePart = mainPart.AddImagePart(imageType);
        using (var stream = File.OpenRead(imagePath))
        {
            imagePart.FeedData(stream);
        }

        var relationshipId = mainPart.GetIdOfPart(imagePart);

        // EMU conversion (1 inch = 914400 EMUs, 1 px ≈ 9525 EMUs at 96dpi)
        var emuWidth = width * 9525;
        var emuHeight = height * 9525;

        // Build the drawing element using raw XML (Open XML SDK's drawing API is verbose)
        var drawingXml = $@"<w:drawing xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main""
            xmlns:wp=""http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing""
            xmlns:a=""http://schemas.openxmlformats.org/drawingml/2006/main""
            xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""
            xmlns:pic=""http://schemas.openxmlformats.org/drawingml/2006/picture"">
            <wp:inline distT=""0"" distB=""0"" distL=""0"" distR=""0"">
                <wp:extent cx=""{emuWidth}"" cy=""{emuHeight}""/>
                <wp:docPr id=""1"" name=""Image"" descr=""{System.Security.SecurityElement.Escape(alt)}""/>
                <a:graphic>
                    <a:graphicData uri=""http://schemas.openxmlformats.org/drawingml/2006/picture"">
                        <pic:pic>
                            <pic:nvPicPr>
                                <pic:cNvPr id=""0"" name=""Image""/>
                                <pic:cNvPicPr/>
                            </pic:nvPicPr>
                            <pic:blipFill>
                                <a:blip r:embed=""{relationshipId}""/>
                                <a:stretch><a:fillRect/></a:stretch>
                            </pic:blipFill>
                            <pic:spPr>
                                <a:xfrm>
                                    <a:off x=""0"" y=""0""/>
                                    <a:ext cx=""{emuWidth}"" cy=""{emuHeight}""/>
                                </a:xfrm>
                                <a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
                            </pic:spPr>
                        </pic:pic>
                    </a:graphicData>
                </a:graphic>
            </wp:inline>
        </w:drawing>";

        var paragraph = new Paragraph();
        var run = new Run();
        var drawing = new Drawing(drawingXml);
        run.AppendChild(drawing);
        ElementIdManager.AssignId(run);
        paragraph.AppendChild(run);
        ElementIdManager.AssignId(paragraph);

        return paragraph;
    }

    private static Paragraph CreateHyperlink(JsonElement value, MainDocumentPart mainPart)
    {
        var url = value.GetProperty("url").GetString()
            ?? throw new ArgumentException("Hyperlink must have a 'url' field.");
        var text = value.TryGetProperty("text", out var t) ? t.GetString() ?? url : url;

        // Add hyperlink relationship
        var rel = mainPart.AddHyperlinkRelationship(new Uri(url), true);

        var paragraph = new Paragraph();
        var hyperlinkRun = new Run(
                new RunProperties(
                    new RunStyle { Val = "Hyperlink" },
                    new Color { Val = "0563C1" },
                    new Underline { Val = UnderlineValues.Single }
                ),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve }
            );
        ElementIdManager.AssignId(hyperlinkRun);
        var hyperlink = new Hyperlink(hyperlinkRun)
        {
            Id = rel.Id
        };
        ElementIdManager.AssignId(hyperlink);
        paragraph.AppendChild(hyperlink);
        ElementIdManager.AssignId(paragraph);

        return paragraph;
    }

    private static Paragraph CreatePageBreak()
    {
        var run = new Run(new Break { Type = BreakValues.Page });
        ElementIdManager.AssignId(run);
        var paragraph = new Paragraph(run);
        ElementIdManager.AssignId(paragraph);
        return paragraph;
    }

    private static Paragraph CreateSectionBreak(JsonElement value)
    {
        var type = value.TryGetProperty("break_type", out var bt)
            ? bt.GetString() ?? "nextPage"
            : "nextPage";

        var sectionType = type.ToLowerInvariant() switch
        {
            "nextpage" or "next_page" => SectionMarkValues.NextPage,
            "continuous" => SectionMarkValues.Continuous,
            "evenpage" or "even_page" => SectionMarkValues.EvenPage,
            "oddpage" or "odd_page" => SectionMarkValues.OddPage,
            _ => SectionMarkValues.NextPage
        };

        var paragraph = new Paragraph(
            new ParagraphProperties(
                new SectionProperties(
                    new SectionType { Val = sectionType })));
        ElementIdManager.AssignId(paragraph);
        return paragraph;
    }

    private static OpenXmlElement CreateList(JsonElement value)
    {
        // Lists in OOXML are just paragraphs with numbering properties
        // We return a container that holds multiple paragraphs
        var items = value.GetProperty("items");
        var ordered = value.TryGetProperty("ordered", out var o) && o.GetBoolean();

        // For simplicity, we return the first item as a paragraph
        // The patch engine handles multiple items by inserting each
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            throw new ArgumentException("List must have at least one item.");

        var paragraph = new Paragraph();
        paragraph.ParagraphProperties = new ParagraphProperties
        {
            ParagraphStyleId = new ParagraphStyleId
            {
                Val = ordered ? "ListNumber" : "ListBullet"
            }
        };

        var text = items[0].GetString() ?? "";
        var run = new Run();
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        ElementIdManager.AssignId(run);
        paragraph.AppendChild(run);
        ElementIdManager.AssignId(paragraph);

        return paragraph;
    }

    /// <summary>
    /// Create multiple list item paragraphs from a list value.
    /// </summary>
    public static List<OpenXmlElement> CreateListItems(JsonElement value)
    {
        var items = value.GetProperty("items");
        var ordered = value.TryGetProperty("ordered", out var o) && o.GetBoolean();
        var styleName = ordered ? "ListNumber" : "ListBullet";
        var result = new List<OpenXmlElement>();

        foreach (var item in items.EnumerateArray())
        {
            var paragraph = new Paragraph();
            paragraph.ParagraphProperties = new ParagraphProperties
            {
                ParagraphStyleId = new ParagraphStyleId { Val = styleName }
            };

            var text = item.GetString() ?? item.ToString();
            var run = new Run();
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            ElementIdManager.AssignId(run);
            paragraph.AppendChild(run);
            ElementIdManager.AssignId(paragraph);

            result.Add(paragraph);
        }

        return result;
    }
}
