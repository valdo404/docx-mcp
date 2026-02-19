using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class ReadSectionTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public ReadSectionTests()
    {
        _sessions = TestHelpers.CreateSessionManager();
        _session = _sessions.Create();

        var body = _session.GetBody();

        // Section 1: heading + 2 paragraphs, ended by SectionProperties in a paragraph
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new Text("Section One Title"))));
        body.AppendChild(new Paragraph(new Run(new Text("Section one paragraph 1"))));
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new SectionProperties()),
            new Run(new Text("Section one paragraph 2"))));

        // Section 2: heading + 3 paragraphs
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new Text("Section Two Title"))));
        body.AppendChild(new Paragraph(new Run(new Text("Section two paragraph 1"))));
        body.AppendChild(new Paragraph(new Run(new Text("Section two paragraph 2"))));
        body.AppendChild(new Paragraph(new Run(new Text("Section two paragraph 3"))));

        // Final SectionProperties as direct child of body (marks end of last section)
        body.AppendChild(new SectionProperties());

        TestHelpers.PersistBaseline(_sessions, _session);
    }

    [Fact]
    public void ListSectionsReturnsOverview()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(_sessions, _session.Id);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(2, doc.RootElement.GetProperty("section_count").GetInt32());

        var sections = doc.RootElement.GetProperty("sections");
        Assert.Equal(2, sections.GetArrayLength());

        // Section 0
        var s0 = sections[0];
        Assert.Equal(0, s0.GetProperty("index").GetInt32());
        Assert.Equal(3, s0.GetProperty("element_count").GetInt32());
        Assert.Equal("Section One Title", s0.GetProperty("first_heading").GetString());

        // Section 1
        var s1 = sections[1];
        Assert.Equal(1, s1.GetProperty("index").GetInt32());
        Assert.Equal(4, s1.GetProperty("element_count").GetInt32());
        Assert.Equal("Section Two Title", s1.GetProperty("first_heading").GetString());
    }

    [Fact]
    public void ListSectionsWithMinusOne()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(_sessions, _session.Id, section_index: -1);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(2, doc.RootElement.GetProperty("section_count").GetInt32());
    }

    [Fact]
    public void ReadSectionZeroReturnsContent()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(_sessions, _session.Id, section_index: 0);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(0, doc.RootElement.GetProperty("section").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());

        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());

        // First item is the heading
        Assert.Equal("Section One Title", items[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ReadSectionOneReturnsContent()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(_sessions, _session.Id, section_index: 1);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("section").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("count").GetInt32());

        var items = doc.RootElement.GetProperty("items");
        Assert.Equal("Section Two Title", items[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ReadSectionWithPagination()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(
            _sessions, _session.Id, section_index: 1, offset: 1, limit: 2);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(4, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());

        var items = doc.RootElement.GetProperty("items");
        Assert.Equal("Section two paragraph 1", items[0].GetProperty("text").GetString());
        Assert.Equal("Section two paragraph 2", items[1].GetProperty("text").GetString());
    }

    [Fact]
    public void ReadSectionOutOfRangeReturnsError()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(_sessions, _session.Id, section_index: 5);
        Assert.Contains("Error: Section index 5 out of range", result);
    }

    [Fact]
    public void ReadSectionTextFormat()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(
            _sessions, _session.Id, section_index: 0, format: "text");
        Assert.Contains("Section One Title", result);
        Assert.Contains("Section one paragraph 1", result);
    }

    [Fact]
    public void ReadSectionSummaryFormat()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(
            _sessions, _session.Id, section_index: 0, format: "summary");
        Assert.Contains("Matched 3 element(s)", result);
    }

    [Fact]
    public void ReadSectionLimitClampedTo50()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(
            _sessions, _session.Id, section_index: 1, limit: 200);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(50, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void ReadSectionOffsetBeyondTotalReturnsEmpty()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(
            _sessions, _session.Id, section_index: 0, offset: 100);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void SectionBreakdownShowsElementTypes()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(_sessions, _session.Id);
        using var doc = JsonDocument.Parse(result);

        var s0 = doc.RootElement.GetProperty("sections")[0];
        var breakdown = s0.GetProperty("breakdown");

        Assert.Equal(1, breakdown.GetProperty("headings").GetInt32());
        Assert.Equal(2, breakdown.GetProperty("paragraphs").GetInt32());
    }

    [Fact]
    public void ReadSectionNegativeOffsetCountsFromEnd()
    {
        // Section 1 has 4 elements; -2 means offset = 2
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(
            _sessions, _session.Id, section_index: 1, offset: -2);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(2, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());

        var items = doc.RootElement.GetProperty("items");
        Assert.Equal("Section two paragraph 2", items[0].GetProperty("text").GetString());
        Assert.Equal("Section two paragraph 3", items[1].GetProperty("text").GetString());
    }

    [Fact]
    public void ReadSectionNegativeOffsetLargerThanTotalClampsToZero()
    {
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(
            _sessions, _session.Id, section_index: 1, offset: -100);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(0, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ReadSectionNegativeOffsetWithLimit()
    {
        // Section 1 has 4 elements; -3 means offset = 1, limit = 2
        var result = DocxMcp.Tools.ReadSectionTool.ReadSection(
            _sessions, _session.Id, section_index: 1, offset: -3, limit: 2);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());

        var items = doc.RootElement.GetProperty("items");
        Assert.Equal("Section two paragraph 1", items[0].GetProperty("text").GetString());
    }

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
