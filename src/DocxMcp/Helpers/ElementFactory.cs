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

        return props;
    }

    private static Paragraph CreateParagraph(JsonElement value)
    {
        var paragraph = new Paragraph();

        // Apply paragraph-level style
        if (value.TryGetProperty("style", out var style))
        {
            paragraph.ParagraphProperties = CreateParagraphProperties(style);
        }

        // Create run with text
        if (value.TryGetProperty("text", out var text))
        {
            var run = new Run();

            // Apply run-level style
            if (value.TryGetProperty("style", out var runStyle))
            {
                run.RunProperties = CreateRunProperties(runStyle);
            }

            run.AppendChild(new Text(text.GetString() ?? "") { Space = SpaceProcessingModeValues.Preserve });
            paragraph.AppendChild(run);
        }

        return paragraph;
    }

    private static Paragraph CreateHeading(JsonElement value)
    {
        var level = value.TryGetProperty("level", out var lvl) ? lvl.GetInt32() : 1;
        var text = value.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";

        var paragraph = new Paragraph();
        paragraph.ParagraphProperties = new ParagraphProperties
        {
            ParagraphStyleId = new ParagraphStyleId { Val = $"Heading{level}" }
        };

        var run = new Run();
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.AppendChild(run);

        return paragraph;
    }

    private static Table CreateTable(JsonElement value)
    {
        var table = new Table();

        // Table properties with borders
        var tblProps = new TableProperties();
        var borderStyle = value.TryGetProperty("border_style", out var bs)
            ? bs.GetString() ?? "single"
            : "single";

        if (borderStyle != "none")
        {
            var borderValue = borderStyle switch
            {
                "single" => BorderValues.Single,
                "double" => BorderValues.Double,
                "dashed" => BorderValues.Dashed,
                "dotted" => BorderValues.Dotted,
                _ => BorderValues.Single
            };

            tblProps.TableBorders = new TableBorders(
                new TopBorder { Val = borderValue, Size = 4 },
                new BottomBorder { Val = borderValue, Size = 4 },
                new LeftBorder { Val = borderValue, Size = 4 },
                new RightBorder { Val = borderValue, Size = 4 },
                new InsideHorizontalBorder { Val = borderValue, Size = 4 },
                new InsideVerticalBorder { Val = borderValue, Size = 4 }
            );
        }

        table.AppendChild(tblProps);

        // Headers row
        if (value.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
        {
            var headerRow = new TableRow();
            foreach (var cell in headers.EnumerateArray())
            {
                var tc = new TableCell();
                var p = new Paragraph();
                var r = new Run();
                r.RunProperties = new RunProperties { Bold = new Bold() };
                r.AppendChild(new Text(cell.GetString() ?? "") { Space = SpaceProcessingModeValues.Preserve });
                p.AppendChild(r);
                tc.AppendChild(p);
                headerRow.AppendChild(tc);
            }
            table.AppendChild(headerRow);
        }

        // Data rows
        if (value.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var tableRow = new TableRow();
                if (row.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cell in row.EnumerateArray())
                    {
                        var tc = new TableCell();
                        var p = new Paragraph();
                        var r = new Run();
                        r.AppendChild(new Text(cell.GetString() ?? cell.ToString()) { Space = SpaceProcessingModeValues.Preserve });
                        p.AppendChild(r);
                        tc.AppendChild(p);
                        tableRow.AppendChild(tc);
                    }
                }
                table.AppendChild(tableRow);
            }
        }

        return table;
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
        paragraph.AppendChild(run);

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
        var hyperlink = new Hyperlink(
            new Run(
                new RunProperties(
                    new RunStyle { Val = "Hyperlink" },
                    new Color { Val = "0563C1" },
                    new Underline { Val = UnderlineValues.Single }
                ),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve }
            ))
        {
            Id = rel.Id
        };
        paragraph.AppendChild(hyperlink);

        return paragraph;
    }

    private static Paragraph CreatePageBreak()
    {
        return new Paragraph(
            new Run(
                new Break { Type = BreakValues.Page }));
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

        return new Paragraph(
            new ParagraphProperties(
                new SectionProperties(
                    new SectionType { Val = sectionType })));
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
        paragraph.AppendChild(run);

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
            paragraph.AppendChild(run);

            result.Add(paragraph);
        }

        return result;
    }
}
