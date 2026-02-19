using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class CountToolTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public CountToolTests()
    {
        _sessions = TestHelpers.CreateSessionManager();
        _session = _sessions.Create();

        var body = _session.GetBody();

        // Add a heading
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new Text("Title"))));

        // Add paragraphs
        for (int i = 0; i < 5; i++)
            body.AppendChild(new Paragraph(new Run(new Text($"Paragraph {i}"))));

        // Add a table
        body.AppendChild(new Table(
            new TableRow(
                new TableCell(new Paragraph(new Run(new Text("A")))),
                new TableCell(new Paragraph(new Run(new Text("B"))))),
            new TableRow(
                new TableCell(new Paragraph(new Run(new Text("C")))),
                new TableCell(new Paragraph(new Run(new Text("D")))))));

        TestHelpers.PersistBaseline(_sessions, _session);
    }

    [Fact]
    public void CountBodyReturnsOverview()
    {
        var result = DocxMcp.Tools.CountTool.CountElements(_sessions, _session.Id, "/body");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(6, doc.RootElement.GetProperty("paragraphs").GetInt32()); // heading + 5 paragraphs
        Assert.Equal(1, doc.RootElement.GetProperty("tables").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("headings").GetInt32());
    }

    [Fact]
    public void CountAllParagraphs()
    {
        var result = DocxMcp.Tools.CountTool.CountElements(_sessions, _session.Id, "/body/paragraph[*]");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("/body/paragraph[*]", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(6, doc.RootElement.GetProperty("count").GetInt32()); // heading + 5 paragraphs
    }

    [Fact]
    public void CountAllTables()
    {
        var result = DocxMcp.Tools.CountTool.CountElements(_sessions, _session.Id, "/body/table[*]");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void CountAllHeadings()
    {
        var result = DocxMcp.Tools.CountTool.CountElements(_sessions, _session.Id, "/body/heading[*]");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void CountTableRows()
    {
        var result = DocxMcp.Tools.CountTool.CountElements(_sessions, _session.Id, "/body/table[0]/row[*]");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void CountWithTextFilter()
    {
        var result = DocxMcp.Tools.CountTool.CountElements(
            _sessions, _session.Id, "/body/paragraph[text~='Paragraph']");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(5, doc.RootElement.GetProperty("count").GetInt32());
    }

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
