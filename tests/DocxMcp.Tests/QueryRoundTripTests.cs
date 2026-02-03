using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Helpers;
using DocxMcp.Tools;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests that queried JSON output can be fed back into patch operations
/// (round-trip fidelity), including tabs, runs, and paragraph properties.
/// </summary>
public class QueryRoundTripTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public QueryRoundTripTests()
    {
        _sessions = TestHelpers.CreateSessionManager();
        _session = _sessions.Create();
    }

    [Fact]
    public void QuerySingleRunIncludesRunsArray()
    {
        var body = _session.GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("Single run"))));

        var result = QueryTool.Query(_sessions, _session.Id, "/body/paragraph[0]");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Runs array should always be present, even for single run
        Assert.True(root.TryGetProperty("runs", out var runs));
        Assert.Equal(1, runs.GetArrayLength());
        Assert.Equal("Single run", runs[0].GetProperty("text").GetString());
    }

    [Fact]
    public void QueryTabRunDetectedCorrectly()
    {
        var body = _session.GetBody();
        var p = new Paragraph(
            new Run(new Text("Before") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new TabChar()),
            new Run(new Text("After") { Space = SpaceProcessingModeValues.Preserve }));
        body.AppendChild(p);

        var result = QueryTool.Query(_sessions, _session.Id, "/body/paragraph[0]");
        using var doc = JsonDocument.Parse(result);
        var runs = doc.RootElement.GetProperty("runs");

        Assert.Equal(3, runs.GetArrayLength());

        Assert.Equal("Before", runs[0].GetProperty("text").GetString());

        // Tab run
        Assert.True(runs[1].GetProperty("tab").GetBoolean());
        Assert.Equal("\t", runs[1].GetProperty("text").GetString());

        Assert.Equal("After", runs[2].GetProperty("text").GetString());
    }

    [Fact]
    public void QueryBreakRunDetectedCorrectly()
    {
        var body = _session.GetBody();
        var p = new Paragraph(
            new Run(new Text("Before")),
            new Run(new Break { Type = BreakValues.Page }),
            new Run(new Text("After")));
        body.AppendChild(p);

        var result = QueryTool.Query(_sessions, _session.Id, "/body/paragraph[0]");
        using var doc = JsonDocument.Parse(result);
        var runs = doc.RootElement.GetProperty("runs");

        Assert.Equal(3, runs.GetArrayLength());
        Assert.Equal("page", runs[1].GetProperty("break").GetString());
    }

    [Fact]
    public void QueryParagraphPropertiesIncluded()
    {
        var body = _session.GetBody();
        var p = new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "120", After = "240", Line = "360" },
                new Indentation { Left = "720", Right = "360" }),
            new Run(new Text("Formatted")));
        body.AppendChild(p);

        var result = QueryTool.Query(_sessions, _session.Id, "/body/paragraph[0]");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("properties", out var props));
        Assert.Equal("center", props.GetProperty("alignment").GetString());
        Assert.Equal(120, props.GetProperty("spacing_before").GetInt32());
        Assert.Equal(240, props.GetProperty("spacing_after").GetInt32());
        Assert.Equal(360, props.GetProperty("line_spacing").GetInt32());
        Assert.Equal(720, props.GetProperty("indent_left").GetInt32());
        Assert.Equal(360, props.GetProperty("indent_right").GetInt32());
    }

    [Fact]
    public void QueryTabStopsIncluded()
    {
        var body = _session.GetBody();
        var p = new Paragraph(
            new ParagraphProperties(
                new Tabs(
                    new TabStop { Val = TabStopValues.Center, Position = 4680 },
                    new TabStop { Val = TabStopValues.Right, Position = 9360, Leader = TabStopLeaderCharValues.Dot })),
            new Run(new Text("Left")),
            new Run(new TabChar()),
            new Run(new Text("Center")),
            new Run(new TabChar()),
            new Run(new Text("Right")));
        body.AppendChild(p);

        var result = QueryTool.Query(_sessions, _session.Id, "/body/paragraph[0]");
        using var doc = JsonDocument.Parse(result);
        var props = doc.RootElement.GetProperty("properties");
        var tabs = props.GetProperty("tabs");

        Assert.Equal(2, tabs.GetArrayLength());

        Assert.Equal(4680, tabs[0].GetProperty("position").GetInt32());
        Assert.Equal("center", tabs[0].GetProperty("alignment").GetString());

        Assert.Equal(9360, tabs[1].GetProperty("position").GetInt32());
        Assert.Equal("right", tabs[1].GetProperty("alignment").GetString());
        Assert.Equal("dot", tabs[1].GetProperty("leader").GetString());
    }

    [Fact]
    public void QueryRunStylesPreserved()
    {
        var body = _session.GetBody();
        var p = new Paragraph(
            new Run(
                new RunProperties(
                    new Bold(),
                    new Italic(),
                    new FontSize { Val = "24" },
                    new RunFonts { Ascii = "Arial" },
                    new Color { Val = "FF0000" }),
                new Text("Styled run")));
        body.AppendChild(p);

        var result = QueryTool.Query(_sessions, _session.Id, "/body/paragraph[0]");
        using var doc = JsonDocument.Parse(result);
        var runStyle = doc.RootElement.GetProperty("runs")[0].GetProperty("style");

        Assert.True(runStyle.GetProperty("bold").GetBoolean());
        Assert.True(runStyle.GetProperty("italic").GetBoolean());
        Assert.Equal(12, runStyle.GetProperty("font_size").GetInt32()); // 24 half-points = 12pt
        Assert.Equal("Arial", runStyle.GetProperty("font_name").GetString());
        Assert.Equal("FF0000", runStyle.GetProperty("color").GetString());
    }

    [Fact]
    public void RoundTripCreateThenQueryParagraph()
    {
        // Create a paragraph with runs via patch
        var patchResult = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "add",
            "path": "/body/children/0",
            "value": {
                "type": "paragraph",
                "properties": {
                    "alignment": "right",
                    "spacing_after": 100
                },
                "runs": [
                    {"text": "Title ", "style": {"bold": true, "font_size": 14, "color": "0000FF"}},
                    {"tab": true},
                    {"text": "Subtitle", "style": {"italic": true}}
                ]
            }
        }]
        """);

        Assert.Contains("\"success\": true", patchResult);

        // Query it back
        var queryResult = QueryTool.Query(_sessions, _session.Id, "/body/paragraph[0]");
        using var doc = JsonDocument.Parse(queryResult);
        var root = doc.RootElement;

        // Verify paragraph properties
        Assert.Equal("right", root.GetProperty("properties").GetProperty("alignment").GetString());
        Assert.Equal(100, root.GetProperty("properties").GetProperty("spacing_after").GetInt32());

        // Verify runs
        var runs = root.GetProperty("runs");
        Assert.Equal(3, runs.GetArrayLength());

        // First run
        Assert.Equal("Title ", runs[0].GetProperty("text").GetString());
        var firstStyle = runs[0].GetProperty("style");
        Assert.True(firstStyle.GetProperty("bold").GetBoolean());
        Assert.Equal(14, firstStyle.GetProperty("font_size").GetInt32());
        Assert.Equal("0000FF", firstStyle.GetProperty("color").GetString());

        // Tab run
        Assert.True(runs[1].GetProperty("tab").GetBoolean());

        // Third run
        Assert.Equal("Subtitle", runs[2].GetProperty("text").GetString());
        Assert.True(runs[2].GetProperty("style").GetProperty("italic").GetBoolean());
    }

    [Fact]
    public void RoundTripCreateThenQueryHeading()
    {
        var patchResult = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "add",
            "path": "/body/children/0",
            "value": {
                "type": "heading",
                "level": 2,
                "runs": [
                    {"text": "Senior Engineer  ", "style": {"color": "2E5496"}},
                    {"tab": true, "style": {"color": "2E5496"}},
                    {"text": "ACME Corp", "style": {"bold": true}},
                    {"tab": true, "style": {"bold": true}},
                    {"text": "2020-2023", "style": {"italic": true}}
                ]
            }
        }]
        """);

        Assert.Contains("\"success\": true", patchResult);

        var queryResult = QueryTool.Query(_sessions, _session.Id, "/body/heading[0]");
        using var doc = JsonDocument.Parse(queryResult);
        var heading = doc.RootElement; // Single heading

        Assert.Equal("heading", heading.GetProperty("type").GetString());
        Assert.Equal(2, heading.GetProperty("level").GetInt32());
        Assert.Equal("Heading2", heading.GetProperty("style").GetString());

        var runs = heading.GetProperty("runs");
        Assert.Equal(5, runs.GetArrayLength());

        // Verify tab runs
        Assert.True(runs[1].GetProperty("tab").GetBoolean());
        Assert.Equal("2E5496", runs[1].GetProperty("style").GetProperty("color").GetString());

        Assert.True(runs[3].GetProperty("tab").GetBoolean());
        Assert.True(runs[3].GetProperty("style").GetProperty("bold").GetBoolean());
    }

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
