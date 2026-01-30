using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Helpers;
using DocxMcp.Paths;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class PatchEngineTests : IDisposable
{
    private readonly DocxSession _session;

    public PatchEngineTests()
    {
        _session = DocxSession.Create();

        // Add initial content
        var body = _session.GetBody();
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new Text("Title"))));
        body.AppendChild(new Paragraph(new Run(new Text("First paragraph"))));
        body.AppendChild(new Paragraph(new Run(new Text("Second paragraph"))));
    }

    [Fact]
    public void AddParagraphAtPosition()
    {
        var body = _session.GetBody();
        var mainPart = _session.Document.MainDocumentPart!;

        var value = JsonDocument.Parse("""
            {"type": "paragraph", "text": "Inserted"}
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        body.InsertChildAt(element, 1); // after heading

        var paragraphs = body.Elements<Paragraph>().ToList();
        Assert.Equal(4, paragraphs.Count);
        Assert.Equal("Inserted", paragraphs[1].InnerText);
    }

    [Fact]
    public void CreateHeading()
    {
        var mainPart = _session.Document.MainDocumentPart!;

        var value = JsonDocument.Parse("""
            {"type": "heading", "level": 2, "text": "Subtitle"}
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        Assert.IsType<Paragraph>(element);

        var p = (Paragraph)element;
        Assert.Equal("Heading2", p.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        Assert.Equal("Subtitle", p.InnerText);
    }

    [Fact]
    public void CreateTable()
    {
        var mainPart = _session.Document.MainDocumentPart!;

        var value = JsonDocument.Parse("""
            {
                "type": "table",
                "headers": ["Name", "Age"],
                "rows": [["Alice", "30"], ["Bob", "25"]]
            }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        Assert.IsType<Table>(element);

        var table = (Table)element;
        var rows = table.Elements<TableRow>().ToList();
        Assert.Equal(3, rows.Count); // 1 header + 2 data

        // Header row should have bold text
        var headerCells = rows[0].Elements<TableCell>().ToList();
        Assert.Equal("Name", headerCells[0].InnerText);
        Assert.Equal("Age", headerCells[1].InnerText);
    }

    [Fact]
    public void CreateStyledParagraph()
    {
        var mainPart = _session.Document.MainDocumentPart!;

        var value = JsonDocument.Parse("""
            {"type": "paragraph", "text": "Bold text", "style": {"bold": true, "font_size": 14}}
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var p = (Paragraph)element;
        var run = p.Elements<Run>().First();
        Assert.NotNull(run.RunProperties?.Bold);
        Assert.Equal("28", run.RunProperties?.FontSize?.Val?.Value); // 14pt = 28 half-points
    }

    [Fact]
    public void RemoveElement()
    {
        var body = _session.GetBody();
        var path = DocxPath.Parse("/body/paragraph[text='Second paragraph']");
        var targets = PathResolver.Resolve(path, _session.Document);

        Assert.Single(targets);
        targets[0].Parent?.RemoveChild(targets[0]);

        var remaining = body.Elements<Paragraph>().ToList();
        Assert.Equal(2, remaining.Count); // heading + first paragraph
        Assert.DoesNotContain(remaining, p => p.InnerText == "Second paragraph");
    }

    [Fact]
    public void ReplaceElement()
    {
        var body = _session.GetBody();
        var mainPart = _session.Document.MainDocumentPart!;

        var path = DocxPath.Parse("/body/paragraph[text='First paragraph']");
        var targets = PathResolver.Resolve(path, _session.Document);
        var target = targets[0];

        var value = JsonDocument.Parse("""
            {"type": "paragraph", "text": "Replaced text"}
        """).RootElement;

        var newElement = ElementFactory.CreateFromJson(value, mainPart);
        target.Parent?.ReplaceChild(newElement, target);

        var paragraphs = body.Elements<Paragraph>().ToList();
        Assert.Contains(paragraphs, p => p.InnerText == "Replaced text");
        Assert.DoesNotContain(paragraphs, p => p.InnerText == "First paragraph");
    }

    [Fact]
    public void MoveElement()
    {
        var body = _session.GetBody();

        // Move "Second paragraph" to position 0 (before heading)
        var sourcePath = DocxPath.Parse("/body/paragraph[text='Second paragraph']");
        var sources = PathResolver.Resolve(sourcePath, _session.Document);
        var source = sources[0];

        source.Parent?.RemoveChild(source);
        body.InsertChildAt(source, 0);

        var children = body.Elements<Paragraph>().ToList();
        Assert.Equal("Second paragraph", children[0].InnerText);
        Assert.Equal("Title", children[1].InnerText);
    }

    [Fact]
    public void CreateListItems()
    {
        var value = JsonDocument.Parse("""
            {"type": "list", "items": ["Item A", "Item B", "Item C"], "ordered": false}
        """).RootElement;

        var items = ElementFactory.CreateListItems(value);
        Assert.Equal(3, items.Count);

        foreach (var item in items)
        {
            var p = Assert.IsType<Paragraph>(item);
            Assert.Equal("ListBullet", p.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
