using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class ReadHeadingContentTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public ReadHeadingContentTests()
    {
        _sessions = new SessionManager();
        _session = _sessions.Create();

        var body = _session.GetBody();

        // Build a structured document:
        // H1: Introduction
        //   paragraph: Intro text 1
        //   paragraph: Intro text 2
        //   H2: Background
        //     paragraph: Background detail
        //     H3: History
        //       paragraph: History detail
        //   H2: Scope
        //     paragraph: Scope detail
        // H1: Methods
        //   paragraph: Methods text
        //   table: 2x2
        // H1: Conclusion
        //   paragraph: Conclusion text

        body.AppendChild(MakeHeading(1, "Introduction"));
        body.AppendChild(MakeParagraph("Intro text 1"));
        body.AppendChild(MakeParagraph("Intro text 2"));

        body.AppendChild(MakeHeading(2, "Background"));
        body.AppendChild(MakeParagraph("Background detail"));

        body.AppendChild(MakeHeading(3, "History"));
        body.AppendChild(MakeParagraph("History detail"));

        body.AppendChild(MakeHeading(2, "Scope"));
        body.AppendChild(MakeParagraph("Scope detail"));

        body.AppendChild(MakeHeading(1, "Methods"));
        body.AppendChild(MakeParagraph("Methods text"));
        body.AppendChild(new Table(
            new TableRow(
                new TableCell(new Paragraph(new Run(new Text("A")))),
                new TableCell(new Paragraph(new Run(new Text("B"))))),
            new TableRow(
                new TableCell(new Paragraph(new Run(new Text("C")))),
                new TableCell(new Paragraph(new Run(new Text("D")))))));

        body.AppendChild(MakeHeading(1, "Conclusion"));
        body.AppendChild(MakeParagraph("Conclusion text"));
    }

    // --- Listing mode tests ---

    [Fact]
    public void ListAllHeadingsReturnsHierarchy()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(6, doc.RootElement.GetProperty("heading_count").GetInt32());

        var headings = doc.RootElement.GetProperty("headings");
        Assert.Equal(6, headings.GetArrayLength());

        // First heading: Introduction (H1)
        Assert.Equal("Introduction", headings[0].GetProperty("text").GetString());
        Assert.Equal(1, headings[0].GetProperty("level").GetInt32());

        // Introduction should have sub-headings
        Assert.True(headings[0].TryGetProperty("direct_sub_headings", out var subH));
        Assert.Equal(2, subH.GetArrayLength());
        Assert.Equal("Background", subH[0].GetString());
        Assert.Equal("Scope", subH[1].GetString());
    }

    [Fact]
    public void ListHeadingsFilteredByLevel()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_level: 1);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(3, doc.RootElement.GetProperty("heading_count").GetInt32());

        var headings = doc.RootElement.GetProperty("headings");
        Assert.Equal("Introduction", headings[0].GetProperty("text").GetString());
        Assert.Equal("Methods", headings[1].GetProperty("text").GetString());
        Assert.Equal("Conclusion", headings[2].GetProperty("text").GetString());
    }

    [Fact]
    public void ListHeadingsShowsContentCount()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id);
        using var doc = JsonDocument.Parse(result);

        var headings = doc.RootElement.GetProperty("headings");

        // "Introduction" H1 has: 2 paragraphs + H2:Background + paragraph + H3:History + paragraph + H2:Scope + paragraph = 8 content elements
        var introCount = headings[0].GetProperty("content_elements").GetInt32();
        Assert.Equal(8, introCount);

        // "Methods" H1 has: 1 paragraph + 1 table = 2 content elements
        var methodsCount = headings[4].GetProperty("content_elements").GetInt32();
        Assert.Equal(2, methodsCount);

        // "Conclusion" H1 has: 1 paragraph = 1 content element
        var conclusionCount = headings[5].GetProperty("content_elements").GetInt32();
        Assert.Equal(1, conclusionCount);
    }

    // --- Content retrieval by text ---

    [Fact]
    public void ReadContentByHeadingText()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Methods");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("heading_level").GetInt32());
        Assert.Equal("Methods", doc.RootElement.GetProperty("heading_text").GetString());
        // H1:Methods + paragraph + table = 3 elements
        Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());

        var items = doc.RootElement.GetProperty("items");
        // First item is the heading itself
        Assert.Equal("heading", items[0].GetProperty("type").GetString());
        Assert.Equal("Methods", items[0].GetProperty("text").GetString());
        // Second item is paragraph
        Assert.Equal("paragraph", items[1].GetProperty("type").GetString());
        Assert.Equal("Methods text", items[1].GetProperty("text").GetString());
        // Third item is a table
        Assert.Equal("table", items[2].GetProperty("type").GetString());
    }

    [Fact]
    public void ReadContentByPartialText()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "conclu");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("Conclusion", doc.RootElement.GetProperty("heading_text").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ReadContentByTextIsCaseInsensitive()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "INTRODUCTION");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("Introduction", doc.RootElement.GetProperty("heading_text").GetString());
    }

    [Fact]
    public void ReadContentByTextNotFound()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "NonExistent");
        Assert.Contains("Error: No heading found matching text 'NonExistent'", result);
    }

    // --- Content retrieval by index ---

    [Fact]
    public void ReadContentByHeadingIndex()
    {
        // Index 0 = Introduction (first heading overall)
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_index: 0);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("Introduction", doc.RootElement.GetProperty("heading_text").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("heading_level").GetInt32());
        // H1 + 2 paras + H2:Background + para + H3:History + para + H2:Scope + para = 9 elements total
        Assert.Equal(9, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ReadContentByIndexWithLevelFilter()
    {
        // Index 1 among level-1 headings = Methods
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_index: 1, heading_level: 1);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal("Methods", doc.RootElement.GetProperty("heading_text").GetString());
    }

    [Fact]
    public void ReadContentByIndexOutOfRange()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_index: 99);
        Assert.Contains("Error: Heading index 99 out of range", result);
    }

    // --- Sub-heading inclusion ---

    [Fact]
    public void ReadContentIncludesSubHeadingsByDefault()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Introduction");
        using var doc = JsonDocument.Parse(result);

        // With sub-headings: H1 + 2 paras + H2:Background + para + H3:History + para + H2:Scope + para = 9
        Assert.Equal(9, doc.RootElement.GetProperty("total").GetInt32());

        // Verify sub-headings are in the result
        var items = doc.RootElement.GetProperty("items");
        var foundBackground = false;
        var foundHistory = false;
        var foundScope = false;
        for (int i = 0; i < items.GetArrayLength(); i++)
        {
            var text = items[i].GetProperty("text").GetString();
            if (text == "Background") foundBackground = true;
            if (text == "History") foundHistory = true;
            if (text == "Scope") foundScope = true;
        }
        Assert.True(foundBackground);
        Assert.True(foundHistory);
        Assert.True(foundScope);
    }

    [Fact]
    public void ReadContentExcludesSubHeadings()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Introduction", include_sub_headings: false);
        using var doc = JsonDocument.Parse(result);

        // Without sub-headings: H1 + 2 paragraphs only (stops at H2:Background)
        Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ReadSubHeadingContentStopsAtSameLevel()
    {
        // H2: Background content should stop at H2: Scope
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Background");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(2, doc.RootElement.GetProperty("heading_level").GetInt32());
        // H2:Background + para + H3:History + para = 4 elements
        Assert.Equal(4, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ReadSubHeadingExcludesNestedSubHeadings()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Background", include_sub_headings: false);
        using var doc = JsonDocument.Parse(result);

        // H2:Background + para only (stops at H3:History)
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    // --- Pagination ---

    [Fact]
    public void ReadContentWithPagination()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Introduction",
            offset: 1, limit: 3);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(9, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());

        var items = doc.RootElement.GetProperty("items");
        // Offset 1 skips the heading, so first item is "Intro text 1"
        Assert.Equal("Intro text 1", items[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ReadContentWithNegativeOffset()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Introduction",
            offset: -2);
        using var doc = JsonDocument.Parse(result);

        // -2 on 9 elements => offset = 7
        Assert.Equal(7, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ReadContentOffsetBeyondTotalReturnsEmpty()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Conclusion",
            offset: 100);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void ReadContentLimitClampedTo50()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Introduction",
            limit: 200);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(50, doc.RootElement.GetProperty("limit").GetInt32());
    }

    // --- Output formats ---

    [Fact]
    public void ReadContentTextFormat()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Methods", format: "text");

        Assert.Contains("Methods", result);
        Assert.Contains("Methods text", result);
    }

    [Fact]
    public void ReadContentSummaryFormat()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Methods", format: "summary");

        Assert.Contains("Matched 3 element(s):", result);
        Assert.Contains("heading1:", result);
        Assert.Contains("paragraph:", result);
        Assert.Contains("table:", result);
    }

    // --- Edge cases ---

    [Fact]
    public void ReadLastHeadingIncludesTrailingContent()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Conclusion");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal("Conclusion", items[0].GetProperty("text").GetString());
        Assert.Equal("Conclusion text", items[1].GetProperty("text").GetString());
    }

    [Fact]
    public void ReadH3HeadingContent()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "History");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(3, doc.RootElement.GetProperty("heading_level").GetInt32());
        // H3:History + paragraph (stops at H2:Scope which is higher level)
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void ReadHeadingByTextWithLevelFilter()
    {
        // "Background" exists at level 2; searching at level 1 should not find it
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_text: "Background", heading_level: 1);
        Assert.Contains("Error: No heading found matching text 'Background' at level 1", result);
    }

    [Fact]
    public void ListHeadingsLevel2Only()
    {
        var result = DocxMcp.Tools.ReadHeadingContentTool.ReadHeadingContent(
            _sessions, _session.Id, heading_level: 2);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(2, doc.RootElement.GetProperty("heading_count").GetInt32());
        var headings = doc.RootElement.GetProperty("headings");
        Assert.Equal("Background", headings[0].GetProperty("text").GetString());
        Assert.Equal("Scope", headings[1].GetProperty("text").GetString());
    }

    // --- Helper methods ---

    private static Paragraph MakeHeading(int level, string text)
    {
        return new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{level}" }),
            new Run(new Text(text)));
    }

    private static Paragraph MakeParagraph(string text)
    {
        return new Paragraph(new Run(new Text(text)));
    }

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
