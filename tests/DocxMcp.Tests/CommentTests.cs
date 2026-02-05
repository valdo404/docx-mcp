using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Helpers;
using DocxMcp.Tools;
using Xunit;

namespace DocxMcp.Tests;

public class CommentTests : IDisposable
{
    private readonly string _tempDir;

    public CommentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docx-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private SessionManager CreateManager() => TestHelpers.CreateSessionManager();

    private static string AddParagraphPatch(string text) =>
        $"[{{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{{\"type\":\"paragraph\",\"text\":\"{text}\"}}}}]";

    // --- Core operations ---

    [Fact]
    public void AddComment_ParagraphLevel_CreatesAllElements()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello world"));

        var result = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Needs revision");
        Assert.Contains("Comment 0 added", result);

        var doc = mgr.Get(id).Document;

        // Verify Comment exists in comments.xml
        var commentsPart = doc.MainDocumentPart!.WordprocessingCommentsPart;
        Assert.NotNull(commentsPart);
        var comment = commentsPart!.Comments!.Elements<Comment>().FirstOrDefault();
        Assert.NotNull(comment);
        Assert.Equal("0", comment!.Id?.Value);
        Assert.Equal("AI Assistant", comment.Author?.Value);

        // Verify RangeStart exists in paragraph
        var body = doc.MainDocumentPart!.Document!.Body!;
        var para = body.Elements<Paragraph>().First();
        Assert.NotNull(para.Descendants<CommentRangeStart>().FirstOrDefault(s => s.Id?.Value == "0"));

        // Verify RangeEnd exists
        Assert.NotNull(para.Descendants<CommentRangeEnd>().FirstOrDefault(s => s.Id?.Value == "0"));

        // Verify CommentReference run exists
        Assert.NotNull(para.Descendants<CommentReference>().FirstOrDefault(s => s.Id?.Value == "0"));
    }

    [Fact]
    public void AddComment_TextLevel_AnchorsCorrectly()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello beautiful world"));

        var result = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Nice word",
            anchor_text: "beautiful");
        Assert.Contains("Comment 0 added", result);

        var doc = mgr.Get(id).Document;
        var body = doc.MainDocumentPart!.Document!.Body!;
        var para = body.Elements<Paragraph>().First();

        // Verify anchoring markers exist
        var rangeStart = para.Descendants<CommentRangeStart>().FirstOrDefault(s => s.Id?.Value == "0");
        var rangeEnd = para.Descendants<CommentRangeEnd>().FirstOrDefault(s => s.Id?.Value == "0");
        Assert.NotNull(rangeStart);
        Assert.NotNull(rangeEnd);

        // Verify the anchored text is "beautiful"
        var anchoredText = CommentHelper.GetAnchoredText(para, "0");
        Assert.Equal("beautiful", anchoredText);
    }

    [Fact]
    public void AddComment_CrossRun_SplitsRunsCorrectly()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        // Create paragraph with two runs: "Hello " and "world today"
        var patches = "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"runs\":[{\"text\":\"Hello \"},{\"text\":\"world today\"}]}}]";
        PatchTool.ApplyPatch(mgr, null, id, patches);

        // Anchor to text that crosses the run boundary
        var result = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Spans runs",
            anchor_text: "lo world");
        Assert.Contains("Comment 0 added", result);

        var doc = mgr.Get(id).Document;
        var body = doc.MainDocumentPart!.Document!.Body!;
        var para = body.Elements<Paragraph>().First();

        var anchoredText = CommentHelper.GetAnchoredText(para, "0");
        Assert.Equal("lo world", anchoredText);
    }

    [Fact]
    public void AddComment_MultiParagraphText_CreatesMultipleParagraphs()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Test"));

        var result = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Line 1\nLine 2");
        Assert.Contains("Comment 0 added", result);

        var doc = mgr.Get(id).Document;
        var commentsPart = doc.MainDocumentPart!.WordprocessingCommentsPart!;
        var comment = commentsPart.Comments!.Elements<Comment>().First();

        var paragraphs = comment.Elements<Paragraph>().ToList();
        Assert.Equal(2, paragraphs.Count);
        Assert.Equal("Line 1", paragraphs[0].InnerText);
        Assert.Equal("Line 2", paragraphs[1].InnerText);
    }

    [Fact]
    public void AddComment_CustomAuthorAndInitials()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Test"));

        var result = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Review this",
            author: "John Doe", initials: "JD");
        Assert.Contains("'John Doe'", result);

        var doc = mgr.Get(id).Document;
        var comment = doc.MainDocumentPart!.WordprocessingCommentsPart!
            .Comments!.Elements<Comment>().First();
        Assert.Equal("John Doe", comment.Author?.Value);
        Assert.Equal("JD", comment.Initials?.Value);
    }

    [Fact]
    public void AddComment_DefaultAuthorAndInitials()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Test"));

        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Default author test");

        var doc = mgr.Get(id).Document;
        var comment = doc.MainDocumentPart!.WordprocessingCommentsPart!
            .Comments!.Elements<Comment>().First();
        Assert.Equal("AI Assistant", comment.Author?.Value);
        Assert.Equal("AI", comment.Initials?.Value);
    }

    // --- List tests ---

    [Fact]
    public void ListComments_ReturnsAllMetadata()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello world"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Test comment",
            anchor_text: "world", author: "Tester", initials: "T");

        var result = CommentTools.CommentList(mgr, id);
        var json = JsonDocument.Parse(result).RootElement;

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        var comments = json.GetProperty("comments");
        Assert.Equal(1, comments.GetArrayLength());

        var c = comments[0];
        Assert.Equal(0, c.GetProperty("id").GetInt32());
        Assert.Equal("Tester", c.GetProperty("author").GetString());
        Assert.Equal("T", c.GetProperty("initials").GetString());
        Assert.Equal("Test comment", c.GetProperty("text").GetString());
        Assert.Equal("world", c.GetProperty("anchored_text").GetString());
    }

    [Fact]
    public void ListComments_AuthorFilter_CaseInsensitive()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Text A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Text B"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "By Alice", author: "Alice");
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[1]", "By Bob", author: "Bob");

        var result = CommentTools.CommentList(mgr, id, author: "alice");
        var json = JsonDocument.Parse(result).RootElement;

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal("Alice", json.GetProperty("comments")[0].GetProperty("author").GetString());
    }

    [Fact]
    public void ListComments_Pagination()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        for (int i = 0; i < 5; i++)
        {
            PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch($"Para {i}"));
            CommentTools.CommentAdd(mgr, id, $"/body/paragraph[{i}]", $"Comment {i}");
        }

        var result = CommentTools.CommentList(mgr, id, offset: 2, limit: 2);
        var json = JsonDocument.Parse(result).RootElement;

        Assert.Equal(5, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.Equal(2, json.GetProperty("offset").GetInt32());
    }

    // --- Delete tests ---

    [Fact]
    public void DeleteComment_ById_RemovesAllElements()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello world"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Test");

        var deleteResult = CommentTools.CommentDelete(mgr, id, comment_id: 0);
        Assert.Contains("Deleted 1", deleteResult);

        var doc = mgr.Get(id).Document;

        // Comment should be gone
        var commentsPart = doc.MainDocumentPart!.WordprocessingCommentsPart;
        Assert.Empty(commentsPart!.Comments!.Elements<Comment>());

        // Anchoring should be gone
        var body = doc.MainDocumentPart!.Document!.Body!;
        Assert.Empty(body.Descendants<CommentRangeStart>());
        Assert.Empty(body.Descendants<CommentRangeEnd>());
        Assert.Empty(body.Descendants<CommentReference>());
    }

    [Fact]
    public void DeleteComment_ByAuthor_RemovesOnlyMatching()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Text A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Text B"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "By Alice", author: "Alice");
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[1]", "By Bob", author: "Bob");

        var result = CommentTools.CommentDelete(mgr, id, author: "Alice");
        Assert.Contains("Deleted 1", result);

        // Bob's comment should remain
        var listResult = CommentTools.CommentList(mgr, id);
        var json = JsonDocument.Parse(listResult).RootElement;
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal("Bob", json.GetProperty("comments")[0].GetProperty("author").GetString());
    }

    [Fact]
    public void DeleteComment_NonExistent_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = CommentTools.CommentDelete(mgr, id, comment_id: 999);
        Assert.Contains("Error", result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public void DeleteComment_NoParams_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = CommentTools.CommentDelete(mgr, id);
        Assert.Contains("Error", result);
        Assert.Contains("At least one", result);
    }

    // --- WAL integration ---

    [Fact]
    public void AddComment_Undo_RemovesComment_Redo_RestoresIt()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello world"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Test comment");

        // Verify comment exists
        var listResult1 = CommentTools.CommentList(mgr, id);
        Assert.Contains("\"total\": 1", listResult1);

        // Undo the comment add
        mgr.Undo(id);

        // Comment should be gone
        var doc2 = mgr.Get(id).Document;
        var commentsPart2 = doc2.MainDocumentPart!.WordprocessingCommentsPart;
        var hasComments = commentsPart2?.Comments?.Elements<Comment>().Any() ?? false;
        Assert.False(hasComments);

        // Redo should restore it
        mgr.Redo(id);

        var doc3 = mgr.Get(id).Document;
        var commentsPart3 = doc3.MainDocumentPart!.WordprocessingCommentsPart;
        Assert.NotNull(commentsPart3);
        Assert.Single(commentsPart3!.Comments!.Elements<Comment>());
    }

    [Fact]
    public void DeleteComment_Undo_RestoresComment()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello world"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Test comment");
        CommentTools.CommentDelete(mgr, id, comment_id: 0);

        // Comment should be gone
        var doc1 = mgr.Get(id).Document;
        Assert.Empty(doc1.MainDocumentPart!.WordprocessingCommentsPart!.Comments!.Elements<Comment>());

        // Undo the delete
        mgr.Undo(id);

        // Comment should be back
        var doc2 = mgr.Get(id).Document;
        var commentsPart = doc2.MainDocumentPart!.WordprocessingCommentsPart;
        Assert.NotNull(commentsPart);
        Assert.Single(commentsPart!.Comments!.Elements<Comment>());
    }

    // --- Query enrichment ---

    [Fact]
    public void Query_ParagraphWithComment_HasCommentsArray()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Some text with feedback"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Needs revision");

        var result = QueryTool.Query(mgr, id, "/body/paragraph[0]");
        var json = JsonDocument.Parse(result).RootElement;

        Assert.True(json.TryGetProperty("comments", out var comments));
        Assert.Equal(1, comments.GetArrayLength());
        Assert.Equal(0, comments[0].GetProperty("id").GetInt32());
        Assert.Equal("AI Assistant", comments[0].GetProperty("author").GetString());
        Assert.Equal("Needs revision", comments[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Query_ParagraphWithoutComment_NoCommentsField()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Clean paragraph"));

        var result = QueryTool.Query(mgr, id, "/body/paragraph[0]");
        var json = JsonDocument.Parse(result).RootElement;

        Assert.False(json.TryGetProperty("comments", out _));
    }

    // --- ID allocation ---

    [Fact]
    public void CommentIds_AreSequential()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Para 0"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Para 1"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Para 2"));

        var r0 = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "C0");
        var r1 = CommentTools.CommentAdd(mgr, id, "/body/paragraph[1]", "C1");
        var r2 = CommentTools.CommentAdd(mgr, id, "/body/paragraph[2]", "C2");

        Assert.Contains("Comment 0", r0);
        Assert.Contains("Comment 1", r1);
        Assert.Contains("Comment 2", r2);
    }

    [Fact]
    public void CommentIds_AfterDeletion_NoReuse()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Para 0"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Para 1"));

        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "C0");
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[1]", "C1");

        // Delete comment 0
        CommentTools.CommentDelete(mgr, id, comment_id: 0);

        // Next ID should be 2 (max existing=1, +1=2), not 0
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Para 2"));
        var r = CommentTools.CommentAdd(mgr, id, "/body/paragraph[2]", "C2");
        Assert.Contains("Comment 2", r);
    }

    // --- Error cases ---

    [Fact]
    public void AddComment_PathResolvesToZero_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Test");
        Assert.Contains("Error", result);
    }

    [Fact]
    public void AddComment_PathResolvesToMultiple_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));

        var result = CommentTools.CommentAdd(mgr, id, "/body/paragraph[*]", "Test");
        Assert.Contains("Error", result);
        Assert.Contains("must resolve to exactly 1", result);
    }

    [Fact]
    public void AddComment_AnchorTextNotFound_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello world"));

        var result = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Test",
            anchor_text: "nonexistent");
        Assert.Contains("Error", result);
        Assert.Contains("not found", result);
    }

    // --- WAL replay across restart ---
    // Note: These tests verify persistence via gRPC storage server

    [Fact]
    public void AddComment_SurvivesRestart_ThenUndo()
    {
        // Use explicit tenant so second manager can find the session
        var tenantId = $"test-comment-restart-{Guid.NewGuid():N}";
        var mgr = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello world"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Persisted comment");

        // Don't close - sessions auto-persist to gRPC storage
        // Simulating a restart: create new manager with same tenant
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        var restored = mgr2.RestoreSessions();
        Assert.Equal(1, restored);

        // Comment should be present after restore
        var listResult = CommentTools.CommentList(mgr2, id);
        Assert.Contains("Persisted comment", listResult);
        Assert.Contains("\"total\": 1", listResult);

        // Undo should work
        var undoResult = mgr2.Undo(id);
        Assert.Equal(1, undoResult.Position); // back to just the paragraph

        // Comment should be gone
        var listResult2 = CommentTools.CommentList(mgr2, id);
        Assert.Contains("\"total\": 0", listResult2);
    }

    [Fact]
    public void AddComment_OnOpenedFile_SurvivesRestart_ThenUndo()
    {
        // Use explicit tenant so second manager can find the session
        var tenantId = $"test-comment-file-restart-{Guid.NewGuid():N}";

        // Create a temp docx file with content, then open it (simulates real file usage)
        var tempFile = Path.Combine(_tempDir, "test.docx");

        // Create file via a session, save, close (this session is intentionally discarded)
        var mgr0 = CreateManager();
        var s0 = mgr0.Create();
        PatchTool.ApplyPatch(mgr0, null, s0.Id, AddParagraphPatch("Paragraph one"));
        PatchTool.ApplyPatch(mgr0, null, s0.Id, AddParagraphPatch("Paragraph two"));
        mgr0.Save(s0.Id, tempFile);
        mgr0.Close(s0.Id);

        // Open the file (like mcptools document_open)
        var mgr = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr.Open(tempFile);
        var id = session.Id;

        var addResult = CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Review this paragraph");
        Assert.Contains("Comment 0 added", addResult);

        // Don't close - simulating a restart: create new manager with same tenant
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        var restored = mgr2.RestoreSessions();
        Assert.Equal(1, restored);

        // Comment should be present
        var list1 = CommentTools.CommentList(mgr2, id);
        Assert.Contains("\"total\": 1", list1);
        Assert.Contains("Review this paragraph", list1);

        // Undo should work
        var undo = mgr2.Undo(id);
        Assert.Contains("Undid", undo.Message);

        // Comment should be gone
        var list2 = CommentTools.CommentList(mgr2, id);
        Assert.Contains("\"total\": 0", list2);
    }

    // --- Query enrichment with anchored text ---

    [Fact]
    public void Query_TextLevelComment_HasAnchoredText()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Some text with feedback"));
        CommentTools.CommentAdd(mgr, id, "/body/paragraph[0]", "Fix this",
            anchor_text: "with feedback");

        var result = QueryTool.Query(mgr, id, "/body/paragraph[0]");
        var json = JsonDocument.Parse(result).RootElement;

        Assert.True(json.TryGetProperty("comments", out var comments));
        var c = comments[0];
        Assert.Equal("with feedback", c.GetProperty("anchored_text").GetString());
    }
}
