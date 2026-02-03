using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Helpers;
using DocxMcp.Persistence;
using DocxMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocxMcp.Tests;

public class StyleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionStore _store;

    public StyleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docx-mcp-tests", Guid.NewGuid().ToString("N"));
        _store = new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private SessionManager CreateManager() =>
        new SessionManager(_store, NullLogger<SessionManager>.Instance);

    private static string AddParagraphPatch(string text) =>
        $"[{{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{{\"type\":\"paragraph\",\"text\":\"{text}\"}}}}]";

    private static string AddStyledParagraphPatch(string text, string runStyle) =>
        $"[{{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{{\"type\":\"paragraph\",\"text\":\"{text}\",\"style\":{runStyle}}}}}]";

    private static string AddTablePatch() =>
        "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"table\",\"headers\":[\"H1\",\"H2\"],\"rows\":[[\"A\",\"B\"],[\"C\",\"D\"]]}}]";

    // =========================
    // Run merge tests
    // =========================

    [Fact]
    public void StyleElement_AddBold_PreservesItalic()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddStyledParagraphPatch("test", "{\"italic\":true}"));

        var result = StyleTools.StyleElement(mgr, id, "{\"bold\":true}");
        Assert.Contains("Styled", result);

        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.NotNull(run.RunProperties?.Bold);
        Assert.NotNull(run.RunProperties?.Italic);
    }

    [Fact]
    public void StyleElement_RemoveBold()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddStyledParagraphPatch("test", "{\"bold\":true}"));

        StyleTools.StyleElement(mgr, id, "{\"bold\":false}");

        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.Null(run.RunProperties?.Bold);
    }

    [Fact]
    public void StyleElement_SetColor()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleElement(mgr, id, "{\"color\":\"FF0000\"}");

        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.Equal("FF0000", run.RunProperties?.Color?.Val?.Value);
    }

    [Fact]
    public void StyleElement_NullRemovesColor()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddStyledParagraphPatch("test", "{\"color\":\"00FF00\"}"));

        StyleTools.StyleElement(mgr, id, "{\"color\":null}");

        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.Null(run.RunProperties?.Color);
    }

    [Fact]
    public void StyleElement_SetFontSizeAndName()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleElement(mgr, id, "{\"font_size\":14,\"font_name\":\"Arial\"}");

        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.Equal("28", run.RunProperties?.FontSize?.Val?.Value); // 14pt * 2 = 28 half-points
        Assert.Equal("Arial", run.RunProperties?.RunFonts?.Ascii?.Value);
    }

    [Fact]
    public void StyleElement_SetHighlight()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleElement(mgr, id, "{\"highlight\":\"yellow\"}");

        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.Equal(HighlightColorValues.Yellow, run.RunProperties?.Highlight?.Val?.Value);
    }

    [Fact]
    public void StyleElement_SetVerticalAlign()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleElement(mgr, id, "{\"vertical_align\":\"superscript\"}");

        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.Equal(VerticalPositionValues.Superscript, run.RunProperties?.VerticalTextAlignment?.Val?.Value);
    }

    [Fact]
    public void StyleElement_SetUnderlineAndStrike()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleElement(mgr, id, "{\"underline\":true,\"strike\":true}");

        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.NotNull(run.RunProperties?.Underline);
        Assert.NotNull(run.RunProperties?.Strike);
    }

    // =========================
    // Paragraph merge tests
    // =========================

    [Fact]
    public void StyleParagraph_Alignment_PreservesIndent()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        // Add paragraph, then set indent via patch
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));
        PatchTool.ApplyPatch(mgr, null, id,
            "[{\"op\":\"replace\",\"path\":\"/body/paragraph[0]/style\",\"value\":{\"indent_left\":720}}]");

        // Now merge alignment — indent should be preserved
        StyleTools.StyleParagraph(mgr, id, "{\"alignment\":\"center\"}", "/body/paragraph[0]");

        var para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal(JustificationValues.Center, para.ParagraphProperties?.Justification?.Val?.Value);
        Assert.Equal("720", para.ParagraphProperties?.Indentation?.Left?.Value);
    }

    [Fact]
    public void StyleParagraph_CompoundSpacingMerge()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        // Set spacing_before
        StyleTools.StyleParagraph(mgr, id, "{\"spacing_before\":200}", "/body/paragraph[0]");

        var para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal("200", para.ParagraphProperties?.SpacingBetweenLines?.Before?.Value);

        // Now set spacing_after — spacing_before should be preserved
        StyleTools.StyleParagraph(mgr, id, "{\"spacing_after\":100}", "/body/paragraph[0]");

        para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal("200", para.ParagraphProperties?.SpacingBetweenLines?.Before?.Value);
        Assert.Equal("100", para.ParagraphProperties?.SpacingBetweenLines?.After?.Value);
    }

    [Fact]
    public void StyleParagraph_Shading()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleParagraph(mgr, id, "{\"shading\":\"FFFF00\"}");

        var para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal("FFFF00", para.ParagraphProperties?.Shading?.Fill?.Value);
    }

    [Fact]
    public void StyleParagraph_SetParagraphStyle()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleParagraph(mgr, id, "{\"style\":\"Heading1\"}");

        var para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal("Heading1", para.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
    }

    [Fact]
    public void StyleParagraph_CompoundIndentMerge()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleParagraph(mgr, id, "{\"indent_left\":720}", "/body/paragraph[0]");
        StyleTools.StyleParagraph(mgr, id, "{\"indent_first_line\":360}", "/body/paragraph[0]");

        var para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal("720", para.ParagraphProperties?.Indentation?.Left?.Value);
        Assert.Equal("360", para.ParagraphProperties?.Indentation?.FirstLine?.Value);
    }

    // =========================
    // Table merge tests
    // =========================

    [Fact]
    public void StyleTable_BorderStyle()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleTable(mgr, id, style: "{\"border_style\":\"double\"}");

        var table = mgr.Get(id).GetBody().Descendants<Table>().First();
        var borders = table.GetFirstChild<TableProperties>()?.TableBorders;
        Assert.NotNull(borders);
        Assert.Equal(BorderValues.Double, borders!.TopBorder?.Val?.Value);
    }

    [Fact]
    public void StyleTable_CellShadingOnAllCells()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleTable(mgr, id, cell_style: "{\"shading\":\"F0F0F0\"}");

        var cells = mgr.Get(id).GetBody().Descendants<TableCell>().ToList();
        Assert.True(cells.Count >= 4); // headers + data
        foreach (var cell in cells)
        {
            Assert.Equal("F0F0F0", cell.GetFirstChild<TableCellProperties>()?.Shading?.Fill?.Value);
        }
    }

    [Fact]
    public void StyleTable_RowHeight()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleTable(mgr, id, row_style: "{\"height\":400}");

        var rows = mgr.Get(id).GetBody().Descendants<TableRow>().ToList();
        foreach (var row in rows)
        {
            var h = row.TableRowProperties?.GetFirstChild<TableRowHeight>();
            Assert.NotNull(h);
            Assert.Equal(400u, h!.Val?.Value);
        }
    }

    [Fact]
    public void StyleTable_IsHeader()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleTable(mgr, id, row_style: "{\"is_header\":true}");

        var rows = mgr.Get(id).GetBody().Descendants<TableRow>().ToList();
        foreach (var row in rows)
        {
            Assert.NotNull(row.TableRowProperties?.GetFirstChild<TableHeader>());
        }
    }

    [Fact]
    public void StyleTable_TableAlignment()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleTable(mgr, id, style: "{\"table_alignment\":\"center\"}");

        var table = mgr.Get(id).GetBody().Descendants<Table>().First();
        var props = table.GetFirstChild<TableProperties>();
        Assert.Equal(TableRowAlignmentValues.Center, props?.TableJustification?.Val?.Value);
    }

    [Fact]
    public void StyleTable_CellVerticalAlign()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleTable(mgr, id, cell_style: "{\"vertical_align\":\"center\"}");

        var cells = mgr.Get(id).GetBody().Descendants<TableCell>().ToList();
        foreach (var cell in cells)
        {
            Assert.Equal(TableVerticalAlignmentValues.Center,
                cell.GetFirstChild<TableCellProperties>()?.TableCellVerticalAlignment?.Val?.Value);
        }
    }

    // =========================
    // Document-wide (no path) tests
    // =========================

    [Fact]
    public void StyleElement_NoPath_StylesAllRunsIncludingTables()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("body text"));
        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleElement(mgr, id, "{\"bold\":true}");

        var runs = mgr.Get(id).GetBody().Descendants<Run>().ToList();
        Assert.True(runs.Count > 1);
        foreach (var run in runs)
        {
            Assert.NotNull(run.RunProperties?.Bold);
        }
    }

    [Fact]
    public void StyleParagraph_NoPath_StylesAllParagraphsIncludingTables()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("body text"));
        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleParagraph(mgr, id, "{\"alignment\":\"center\"}");

        var paragraphs = mgr.Get(id).GetBody().Descendants<Paragraph>().ToList();
        Assert.True(paragraphs.Count > 1);
        foreach (var para in paragraphs)
        {
            Assert.Equal(JustificationValues.Center, para.ParagraphProperties?.Justification?.Val?.Value);
        }
    }

    // =========================
    // Batch with [*] selector
    // =========================

    [Fact]
    public void StyleElement_WildcardPath_StylesMatchedRuns()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("first"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("second"));

        StyleTools.StyleElement(mgr, id, "{\"italic\":true}", "/body/paragraph[*]");

        var runs = mgr.Get(id).GetBody().Descendants<Run>().ToList();
        foreach (var run in runs)
        {
            Assert.NotNull(run.RunProperties?.Italic);
        }
    }

    // =========================
    // WAL replay / undo-redo
    // =========================

    [Fact]
    public void StyleElement_UndoRedo_RoundTrip()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        // Style it bold
        StyleTools.StyleElement(mgr, id, "{\"bold\":true}");
        var run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.NotNull(run.RunProperties?.Bold);

        // Undo
        mgr.Undo(id);
        run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.Null(run.RunProperties?.Bold);

        // Redo
        mgr.Redo(id);
        run = mgr.Get(id).GetBody().Descendants<Run>().First();
        Assert.NotNull(run.RunProperties?.Bold);
    }

    [Fact]
    public void StyleParagraph_UndoRedo_RoundTrip()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        StyleTools.StyleParagraph(mgr, id, "{\"alignment\":\"right\"}");
        var para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal(JustificationValues.Right, para.ParagraphProperties?.Justification?.Val?.Value);

        mgr.Undo(id);
        para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Null(para.ParagraphProperties?.Justification);

        mgr.Redo(id);
        para = mgr.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal(JustificationValues.Right, para.ParagraphProperties?.Justification?.Val?.Value);
    }

    [Fact]
    public void StyleTable_UndoRedo_RoundTrip()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddTablePatch());

        StyleTools.StyleTable(mgr, id, style: "{\"border_style\":\"double\"}");
        var table = mgr.Get(id).GetBody().Descendants<Table>().First();
        Assert.Equal(BorderValues.Double, table.GetFirstChild<TableProperties>()?.TableBorders?.TopBorder?.Val?.Value);

        mgr.Undo(id);
        table = mgr.Get(id).GetBody().Descendants<Table>().First();
        // After undo, borders should be single (original from AddTablePatch)
        Assert.NotEqual(BorderValues.Double, table.GetFirstChild<TableProperties>()?.TableBorders?.TopBorder?.Val?.Value ?? BorderValues.Single);

        mgr.Redo(id);
        table = mgr.Get(id).GetBody().Descendants<Table>().First();
        Assert.Equal(BorderValues.Double, table.GetFirstChild<TableProperties>()?.TableBorders?.TopBorder?.Val?.Value);
    }

    // =========================
    // Restart persistence via WAL replay
    // =========================

    [Fact]
    public void StyleElement_PersistsThroughRestart()
    {
        var mgr1 = CreateManager();
        var session = mgr1.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr1, null, id, AddParagraphPatch("persist"));
        StyleTools.StyleElement(mgr1, id, "{\"bold\":true,\"color\":\"00FF00\"}");

        // Simulate restart
        _store.Dispose();
        var store2 = new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);
        var mgr2 = new SessionManager(store2, NullLogger<SessionManager>.Instance);
        mgr2.RestoreSessions();

        var run = mgr2.Get(id).GetBody().Descendants<Run>().First();
        Assert.NotNull(run.RunProperties?.Bold);
        Assert.Equal("00FF00", run.RunProperties?.Color?.Val?.Value);

        store2.Dispose();
    }

    [Fact]
    public void StyleParagraph_PersistsThroughRestart()
    {
        var mgr1 = CreateManager();
        var session = mgr1.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr1, null, id, AddParagraphPatch("persist"));
        StyleTools.StyleParagraph(mgr1, id, "{\"alignment\":\"center\"}");

        _store.Dispose();
        var store2 = new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);
        var mgr2 = new SessionManager(store2, NullLogger<SessionManager>.Instance);
        mgr2.RestoreSessions();

        var para = mgr2.Get(id).GetBody().Descendants<Paragraph>().First();
        Assert.Equal(JustificationValues.Center, para.ParagraphProperties?.Justification?.Val?.Value);

        store2.Dispose();
    }

    [Fact]
    public void StyleTable_PersistsThroughRestart()
    {
        var mgr1 = CreateManager();
        var session = mgr1.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr1, null, id, AddTablePatch());
        StyleTools.StyleTable(mgr1, id, cell_style: "{\"shading\":\"AABBCC\"}");

        _store.Dispose();
        var store2 = new SessionStore(NullLogger<SessionStore>.Instance, _tempDir);
        var mgr2 = new SessionManager(store2, NullLogger<SessionManager>.Instance);
        mgr2.RestoreSessions();

        var cell = mgr2.Get(id).GetBody().Descendants<TableCell>().First();
        Assert.Equal("AABBCC", cell.GetFirstChild<TableCellProperties>()?.Shading?.Fill?.Value);

        store2.Dispose();
    }

    // =========================
    // Error cases
    // =========================

    [Fact]
    public void StyleElement_InvalidJson_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = StyleTools.StyleElement(mgr, id, "not json");
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void StyleElement_BadPath_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("test"));

        var result = StyleTools.StyleElement(mgr, id, "{\"bold\":true}", "/body/paragraph[99]");
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void StyleTable_AllNullStyles_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = StyleTools.StyleTable(mgr, id);
        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public void StyleElement_NotObject_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = StyleTools.StyleElement(mgr, id, "42");
        Assert.Contains("must be a JSON object", result);
    }
}
