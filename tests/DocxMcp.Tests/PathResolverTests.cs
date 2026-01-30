using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Paths;
using Xunit;

namespace DocxMcp.Tests;

public class PathResolverTests : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly WordprocessingDocument _doc;

    public PathResolverTests()
    {
        _stream = new MemoryStream();
        _doc = WordprocessingDocument.Create(_stream, WordprocessingDocumentType.Document);
        var mainPart = _doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());

        var body = mainPart.Document.Body!;

        // Add a heading
        var heading = new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new Text("Title")));
        body.AppendChild(heading);

        // Add two paragraphs
        body.AppendChild(new Paragraph(new Run(new Text("First paragraph"))));
        body.AppendChild(new Paragraph(new Run(new Text("Second paragraph"))));

        // Add a table
        var table = new Table(
            new TableRow(
                new TableCell(new Paragraph(new Run(new Text("A")))),
                new TableCell(new Paragraph(new Run(new Text("B"))))),
            new TableRow(
                new TableCell(new Paragraph(new Run(new Text("C")))),
                new TableCell(new Paragraph(new Run(new Text("D"))))));
        body.AppendChild(table);
    }

    [Fact]
    public void ResolveBody()
    {
        var path = DocxPath.Parse("/body");
        var results = PathResolver.Resolve(path, _doc);
        Assert.Single(results);
        Assert.IsType<Body>(results[0]);
    }

    [Fact]
    public void ResolveParagraphByIndex()
    {
        // Index 0 is the heading, index 1 is "First paragraph"
        var path = DocxPath.Parse("/body/paragraph[1]");
        var results = PathResolver.Resolve(path, _doc);
        Assert.Single(results);
        Assert.Equal("First paragraph", results[0].InnerText);
    }

    [Fact]
    public void ResolveAllParagraphs()
    {
        var path = DocxPath.Parse("/body/paragraph[*]");
        var results = PathResolver.Resolve(path, _doc);
        // Heading + 2 paragraphs = 3
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ResolveHeading()
    {
        var path = DocxPath.Parse("/body/heading[level=1]");
        var results = PathResolver.Resolve(path, _doc);
        Assert.Single(results);
        Assert.Equal("Title", results[0].InnerText);
    }

    [Fact]
    public void ResolveTextContains()
    {
        var path = DocxPath.Parse("/body/paragraph[text~='Second']");
        var results = PathResolver.Resolve(path, _doc);
        Assert.Single(results);
        Assert.Equal("Second paragraph", results[0].InnerText);
    }

    [Fact]
    public void ResolveTable()
    {
        var path = DocxPath.Parse("/body/table[0]");
        var results = PathResolver.Resolve(path, _doc);
        Assert.Single(results);
        Assert.IsType<Table>(results[0]);
    }

    [Fact]
    public void ResolveTableCell()
    {
        var path = DocxPath.Parse("/body/table[0]/row[1]/cell[0]");
        var results = PathResolver.Resolve(path, _doc);
        Assert.Single(results);
        Assert.Equal("C", results[0].InnerText);
    }

    [Fact]
    public void ResolveForInsert()
    {
        var path = DocxPath.Parse("/body/children/0");
        var (parent, index) = PathResolver.ResolveForInsert(path, _doc);
        Assert.IsType<Body>(parent);
        Assert.Equal(0, index);
    }

    [Fact]
    public void ResolveOutOfRangeThrows()
    {
        var path = DocxPath.Parse("/body/paragraph[99]");
        Assert.Throws<InvalidOperationException>(() => PathResolver.Resolve(path, _doc));
    }

    public void Dispose()
    {
        _doc.Dispose();
        _stream.Dispose();
    }
}
