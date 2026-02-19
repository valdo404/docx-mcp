using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class QueryPaginationTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public QueryPaginationTests()
    {
        _sessions = TestHelpers.CreateSessionManager();
        _session = _sessions.Create();

        var body = _session.GetBody();

        // Add 20 paragraphs to test pagination
        for (int i = 0; i < 20; i++)
        {
            body.AppendChild(new Paragraph(new Run(new Text($"Paragraph {i}"))));
        }

        TestHelpers.PersistBaseline(_sessions, _session);
    }

    [Fact]
    public void SingleElementResultHasNoPaginationEnvelope()
    {
        var result = DocxMcp.Tools.QueryTool.Query(_sessions, _session.Id, "/body/paragraph[0]");
        using var doc = JsonDocument.Parse(result);
        // Single element: no pagination wrapper
        Assert.Equal("paragraph", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Paragraph 0", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void WildcardReturnsPaginationEnvelope()
    {
        var result = DocxMcp.Tools.QueryTool.Query(_sessions, _session.Id, "/body/paragraph[*]");
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Equal(20, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(50, doc.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(20, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(20, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void OffsetSkipsElements()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", offset: 5);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(20, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(15, doc.RootElement.GetProperty("count").GetInt32());

        var firstItem = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("Paragraph 5", firstItem.GetProperty("text").GetString());
    }

    [Fact]
    public void LimitRestrictsResultCount()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", limit: 3);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(20, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void OffsetAndLimitCombined()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", offset: 10, limit: 5);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(20, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(10, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("count").GetInt32());

        var firstItem = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("Paragraph 10", firstItem.GetProperty("text").GetString());
    }

    [Fact]
    public void LimitClampedToMax50()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", limit: 100);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(50, doc.RootElement.GetProperty("limit").GetInt32());
        // Only 20 paragraphs, so count is 20
        Assert.Equal(20, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void LimitClampedToMin1()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", limit: 0);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(1, doc.RootElement.GetProperty("limit").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void OffsetBeyondTotalReturnsEmptyItems()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", offset: 100);
        using var doc = JsonDocument.Parse(result);

        Assert.Equal(20, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void TextFormatWithPaginationIncludesHeader()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "text", offset: 0, limit: 5);
        Assert.StartsWith("[5/20 elements, offset 0]", result);
        Assert.Contains("Paragraph 0", result);
        Assert.Contains("Paragraph 4", result);
        Assert.DoesNotContain("Paragraph 5", result);
    }

    [Fact]
    public void SummaryFormatWithPaginationIncludesHeader()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "summary", offset: 0, limit: 3);
        Assert.StartsWith("[3/20 elements, offset 0]", result);
        Assert.Contains("Matched 3 element(s)", result);
    }

    [Fact]
    public void NegativeOffsetCountsFromEnd()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", offset: -5);
        using var doc = JsonDocument.Parse(result);

        // -5 on 20 elements means offset = 15
        Assert.Equal(15, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("count").GetInt32());

        var firstItem = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("Paragraph 15", firstItem.GetProperty("text").GetString());
    }

    [Fact]
    public void NegativeOffsetLargerThanTotalClampsToZero()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", offset: -100);
        using var doc = JsonDocument.Parse(result);

        // -100 on 20 elements clamps to 0
        Assert.Equal(0, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(20, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void NegativeOffsetWithLimit()
    {
        var result = DocxMcp.Tools.QueryTool.Query(
            _sessions, _session.Id, "/body/paragraph[*]", "json", offset: -10, limit: 3);
        using var doc = JsonDocument.Parse(result);

        // -10 on 20 elements means offset = 10
        Assert.Equal(10, doc.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());

        var firstItem = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("Paragraph 10", firstItem.GetProperty("text").GetString());
    }

    [Fact]
    public void WildcardSingleElementHasPaginationEnvelope()
    {
        // Create a session with exactly 1 paragraph
        var sessions = TestHelpers.CreateSessionManager();
        var session = sessions.Create();
        session.GetBody().AppendChild(new Paragraph(new Run(new Text("Only one"))));
        TestHelpers.PersistBaseline(sessions, session);

        var result = DocxMcp.Tools.QueryTool.Query(sessions, session.Id, "/body/paragraph[*]");
        using var doc = JsonDocument.Parse(result);

        // With the fix, [*] with single element should still have pagination envelope
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(1, items.GetArrayLength());

        sessions.Close(session.Id);
    }

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
