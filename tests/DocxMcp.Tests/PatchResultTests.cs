using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests for standardized patch responses, dry_run mode, and replace_text validations.
/// </summary>
public class PatchResultTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public PatchResultTests()
    {
        _sessions = TestHelpers.CreateSessionManager();
        _session = _sessions.Create();

        var body = _session.GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("hello world, hello universe, hello everyone"))));
        body.AppendChild(new Paragraph(new Run(new Text("Second paragraph with hello"))));
    }

    #region JSON Response Format Tests

    [Fact]
    public void ApplyPatch_ReturnsStructuredJson()
    {
        var json = """[{"op": "add", "path": "/body/children/0", "value": {"type": "paragraph", "text": "New"}}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("applied").GetInt32());
        Assert.Equal(1, root.GetProperty("total").GetInt32());

        var ops = root.GetProperty("operations");
        Assert.Equal(1, ops.GetArrayLength());

        var op = ops[0];
        Assert.Equal("add", op.GetProperty("op").GetString());
        Assert.Equal("success", op.GetProperty("status").GetString());
        Assert.True(op.TryGetProperty("created_id", out _));
    }

    [Fact]
    public void ApplyPatch_ErrorReturnsStructuredJson()
    {
        var json = """[{"op": "remove", "path": "/body/paragraph[999]"}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("applied").GetInt32());

        var ops = root.GetProperty("operations");
        var op = ops[0];
        Assert.Equal("error", op.GetProperty("status").GetString());
        Assert.True(op.TryGetProperty("error", out _));
    }

    [Fact]
    public void ApplyPatch_InvalidJsonReturnsStructuredError()
    {
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, "not json");

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.True(root.TryGetProperty("error", out var error));
        Assert.Contains("Invalid JSON", error.GetString());
    }

    [Fact]
    public void ApplyPatch_TooManyOperationsReturnsStructuredError()
    {
        var patches = new List<object>();
        for (int i = 0; i < 11; i++)
        {
            patches.Add(new { op = "add", path = "/body/children/0", value = new { type = "paragraph", text = $"P{i}" } });
        }

        var json = JsonSerializer.Serialize(patches);
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(11, root.GetProperty("total").GetInt32());
        Assert.Contains("Too many operations", root.GetProperty("error").GetString());
    }

    #endregion

    #region Dry Run Tests

    [Fact]
    public void DryRun_DoesNotApplyChanges()
    {
        var body = _session.GetBody();
        var initialCount = body.Elements<Paragraph>().Count();

        var json = """[{"op": "add", "path": "/body/children/0", "value": {"type": "paragraph", "text": "New"}}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json, dry_run: true);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(root.GetProperty("dry_run").GetBoolean());
        Assert.Equal(0, root.GetProperty("applied").GetInt32());
        Assert.Equal(1, root.GetProperty("would_apply").GetInt32());

        // Verify document was not modified
        Assert.Equal(initialCount, body.Elements<Paragraph>().Count());
    }

    [Fact]
    public void DryRun_ReturnsWouldSucceedStatus()
    {
        var json = """[{"op": "remove", "path": "/body/paragraph[0]"}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json, dry_run: true);

        var doc = JsonDocument.Parse(result);
        var ops = doc.RootElement.GetProperty("operations");

        Assert.Equal("would_succeed", ops[0].GetProperty("status").GetString());
    }

    [Fact]
    public void DryRun_ReturnsWouldFailForInvalidPath()
    {
        var json = """[{"op": "remove", "path": "/body/paragraph[999]"}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json, dry_run: true);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("would_fail", root.GetProperty("operations")[0].GetProperty("status").GetString());
    }

    [Fact]
    public void DryRun_ReplaceText_ReturnsMatchCountAndWouldReplace()
    {
        var json = """[{"op": "replace_text", "path": "/body/paragraph[0]", "find": "hello", "replace": "hi", "max_count": 2}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json, dry_run: true);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.Equal("would_succeed", op.GetProperty("status").GetString());
        Assert.Equal(3, op.GetProperty("matches_found").GetInt32()); // "hello" appears 3 times
        Assert.Equal(2, op.GetProperty("would_replace").GetInt32()); // max_count = 2
    }

    #endregion

    #region Replace Text max_count Tests

    [Fact]
    public void ReplaceText_DefaultMaxCountIsOne()
    {
        var json = """[{"op": "replace_text", "path": "/body/paragraph[0]", "find": "hello", "replace": "hi"}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.Equal("success", op.GetProperty("status").GetString());
        Assert.Equal(3, op.GetProperty("matches_found").GetInt32());
        Assert.Equal(1, op.GetProperty("replacements_made").GetInt32());

        // Verify only first occurrence was replaced
        var text = _session.GetBody().Elements<Paragraph>().First().InnerText;
        Assert.Equal("hi world, hello universe, hello everyone", text);
    }

    [Fact]
    public void ReplaceText_MaxCountZero_DoesNothing()
    {
        var originalText = _session.GetBody().Elements<Paragraph>().First().InnerText;

        var json = """[{"op": "replace_text", "path": "/body/paragraph[0]", "find": "hello", "replace": "hi", "max_count": 0}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.Equal("success", op.GetProperty("status").GetString());
        Assert.Equal(3, op.GetProperty("matches_found").GetInt32());
        // replacements_made is omitted when 0 due to JsonIgnoreCondition.WhenWritingDefault
        Assert.False(op.TryGetProperty("replacements_made", out _));

        // Verify document was not modified
        Assert.Equal(originalText, _session.GetBody().Elements<Paragraph>().First().InnerText);
    }

    [Fact]
    public void ReplaceText_MaxCountNegative_ReturnsError()
    {
        var json = """[{"op": "replace_text", "path": "/body/paragraph[0]", "find": "hello", "replace": "hi", "max_count": -1}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.Equal("error", op.GetProperty("status").GetString());
        Assert.Contains("max_count", op.GetProperty("error").GetString());
    }

    [Fact]
    public void ReplaceText_MaxCountHigherThanMatches_ReplacesAll()
    {
        var json = """[{"op": "replace_text", "path": "/body/paragraph[0]", "find": "hello", "replace": "hi", "max_count": 100}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.Equal(3, op.GetProperty("matches_found").GetInt32());
        Assert.Equal(3, op.GetProperty("replacements_made").GetInt32());

        var text = _session.GetBody().Elements<Paragraph>().First().InnerText;
        Assert.Equal("hi world, hi universe, hi everyone", text);
    }

    [Fact]
    public void ReplaceText_MaxCountTwo_ReplacesTwoOccurrences()
    {
        var json = """[{"op": "replace_text", "path": "/body/paragraph[0]", "find": "hello", "replace": "hi", "max_count": 2}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.Equal(3, op.GetProperty("matches_found").GetInt32());
        Assert.Equal(2, op.GetProperty("replacements_made").GetInt32());

        var text = _session.GetBody().Elements<Paragraph>().First().InnerText;
        Assert.Equal("hi world, hi universe, hello everyone", text);
    }

    #endregion

    #region Empty Replace Validation Tests

    [Fact]
    public void ReplaceText_EmptyReplace_ReturnsError()
    {
        var json = """[{"op": "replace_text", "path": "/body/paragraph[0]", "find": "hello", "replace": ""}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());

        var op = root.GetProperty("operations")[0];
        Assert.Equal("error", op.GetProperty("status").GetString());
        Assert.Contains("cannot be empty", op.GetProperty("error").GetString());
    }

    [Fact]
    public void ReplaceText_NullReplace_ReturnsError()
    {
        // JSON null for replace field
        var json = """[{"op": "replace_text", "path": "/body/paragraph[0]", "find": "hello", "replace": null}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.Equal("error", op.GetProperty("status").GetString());
    }

    #endregion

    #region Operation-specific Result Fields Tests

    [Fact]
    public void AddOperation_ReturnsCreatedId()
    {
        var json = """[{"op": "add", "path": "/body/children/0", "value": {"type": "paragraph", "text": "New"}}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.True(op.TryGetProperty("created_id", out var createdId));
        Assert.NotNull(createdId.GetString());
        Assert.NotEmpty(createdId.GetString()!);
    }

    [Fact]
    public void RemoveOperation_ReturnsRemovedId()
    {
        // First add a paragraph via patch so it gets an ID
        var addJson = """[{"op": "add", "path": "/body/children/0", "value": {"type": "paragraph", "text": "Paragraph to remove"}}]""";
        DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, addJson);

        var json = """[{"op": "remove", "path": "/body/paragraph[0]"}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.True(op.TryGetProperty("removed_id", out var removedId));
        Assert.NotNull(removedId.GetString());
    }

    [Fact]
    public void MoveOperation_ReturnsMovedIdAndFrom()
    {
        // First add a paragraph via patch so it gets an ID
        var addJson = """[{"op": "add", "path": "/body/children/999", "value": {"type": "paragraph", "text": "Paragraph to move"}}]""";
        DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, addJson);

        var json = """[{"op": "move", "from": "/body/paragraph[-1]", "path": "/body/children/0"}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.True(op.TryGetProperty("moved_id", out _));
        Assert.Equal("/body/paragraph[-1]", op.GetProperty("from").GetString());
    }

    [Fact]
    public void CopyOperation_ReturnsSourceIdAndCopyId()
    {
        // First add a paragraph via patch so it gets an ID
        var addJson = """[{"op": "add", "path": "/body/children/0", "value": {"type": "paragraph", "text": "Paragraph to copy"}}]""";
        DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, addJson);

        var json = """[{"op": "copy", "from": "/body/paragraph[0]", "path": "/body/children/999"}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.True(op.TryGetProperty("source_id", out _));
        Assert.True(op.TryGetProperty("copy_id", out _));
    }

    [Fact]
    public void RemoveColumnOperation_ReturnsColumnIndexAndRowsAffected()
    {
        // First add a table
        var addTableJson = """[{"op": "add", "path": "/body/children/0", "value": {"type": "table", "headers": ["A", "B", "C"], "rows": [["1", "2", "3"], ["4", "5", "6"]]}}]""";
        DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, addTableJson);

        // Then remove a column
        var json = """[{"op": "remove_column", "path": "/body/table[0]", "column": 1}]""";
        var result = DocxMcp.Tools.PatchTool.ApplyPatch(_sessions, null, _session.Id, json);

        var doc = JsonDocument.Parse(result);
        var op = doc.RootElement.GetProperty("operations")[0];

        Assert.Equal(1, op.GetProperty("column_index").GetInt32());
        Assert.Equal(3, op.GetProperty("rows_affected").GetInt32()); // 1 header + 2 data rows
    }

    #endregion

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
