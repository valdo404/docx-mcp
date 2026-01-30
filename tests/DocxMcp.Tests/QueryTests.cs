using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class QueryTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public QueryTests()
    {
        _sessions = new SessionManager();
        _session = _sessions.Create();

        var body = _session.GetBody();

        // Add a heading
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new Text("Document Title"))));

        // Add paragraphs
        body.AppendChild(new Paragraph(new Run(new Text("First paragraph content"))));
        body.AppendChild(new Paragraph(new Run(new Text("Second paragraph content"))));

        // Add a table
        body.AppendChild(new Table(
            new TableRow(
                new TableCell(new Paragraph(new Run(new Text("H1")))),
                new TableCell(new Paragraph(new Run(new Text("H2"))))),
            new TableRow(
                new TableCell(new Paragraph(new Run(new Text("V1")))),
                new TableCell(new Paragraph(new Run(new Text("V2")))))));
    }

    [Fact]
    public void QueryBodyReturnsStructure()
    {
        var result = DocxMcp.Tools.QueryTool.Query(_sessions, _session.Id, "/body");
        Assert.Contains("paragraph_count", result);
        Assert.Contains("table_count", result);
        Assert.Contains("heading_count", result);
    }

    [Fact]
    public void QueryParagraphByIndex()
    {
        var result = DocxMcp.Tools.QueryTool.Query(_sessions, _session.Id, "/body/paragraph[1]");
        using var doc = JsonDocument.Parse(result);
        var text = doc.RootElement.GetProperty("text").GetString();
        Assert.Equal("First paragraph content", text);
    }

    [Fact]
    public void QueryAllParagraphs()
    {
        var result = DocxMcp.Tools.QueryTool.Query(_sessions, _session.Id, "/body/paragraph[*]");
        using var doc = JsonDocument.Parse(result);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(3, doc.RootElement.GetArrayLength()); // heading + 2 paragraphs
    }

    [Fact]
    public void QueryTable()
    {
        var result = DocxMcp.Tools.QueryTool.Query(_sessions, _session.Id, "/body/table[0]");
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("table", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("rows").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("cols").GetInt32());
    }

    [Fact]
    public void QueryTextFormat()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "text");
        Assert.Contains("Document Title", result);
        Assert.Contains("First paragraph content", result);
        Assert.Contains("Second paragraph content", result);
    }

    [Fact]
    public void QuerySummaryFormat()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "summary");
        Assert.Contains("Matched 3 element(s)", result);
    }

    [Fact]
    public void QueryHeadingByLevel()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/heading[level=1]");
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("heading", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Document Title", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void QueryTextContains()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[text~='Second']");
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("Second paragraph content", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void QueryTableCell()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/table[0]/row[1]/cell[1]");
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("V2", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void QueryMetadata()
    {
        var result = DocxMcp.Tools.QueryTool.Query(_sessions, _session.Id, "/metadata");
        using var doc = JsonDocument.Parse(result);
        // Should have metadata fields
        Assert.True(doc.RootElement.TryGetProperty("title", out _));
        Assert.True(doc.RootElement.TryGetProperty("creator", out _));
    }

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
