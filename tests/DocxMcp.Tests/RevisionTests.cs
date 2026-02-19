using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.ExternalChanges;
using DocxMcp.Helpers;
using DocxMcp.Tools;
using Xunit;

namespace DocxMcp.Tests;

public class RevisionTests
{
    private SessionManager CreateManager() => TestHelpers.CreateSessionManager();

    private SyncManager CreateSyncManager() => TestHelpers.CreateSyncManager();

    private static string AddParagraphPatch(string text) =>
        $"[{{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{{\"type\":\"paragraph\",\"text\":\"{text}\"}}}}]";

    /// <summary>
    /// Create a session with a paragraph containing a tracked insertion (w:ins).
    /// The tracked change is included in the baseline so it's visible via gRPC.
    /// Returns (manager, sessionId, revisionId).
    /// </summary>
    private (SessionManager mgr, string id, int revisionId) CreateSessionWithInsertion()
    {
        var mgr = CreateManager();
        var session = mgr.Create();

        // Build the paragraph with a tracked insertion directly in the session body
        var body = session.GetBody();
        var para = new Paragraph(new Run(new Text("Hello") { Space = SpaceProcessingModeValues.Preserve }));
        var insRun = new InsertedRun
        {
            Id = "100",
            Author = "TestAuthor",
            Date = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)
        };
        insRun.AppendChild(new Run(new Text(" world") { Space = SpaceProcessingModeValues.Preserve }));
        para.AppendChild(insRun);
        body.AppendChild(para);

        // Persist baseline so gRPC storage has the tracked changes
        TestHelpers.PersistBaseline(mgr, session);

        return (mgr, session.Id, 100);
    }

    /// <summary>
    /// Create a session with a paragraph containing a tracked deletion (w:del).
    /// The tracked change is included in the baseline so it's visible via gRPC.
    /// Returns (manager, sessionId, revisionId).
    /// </summary>
    private (SessionManager mgr, string id, int revisionId) CreateSessionWithDeletion()
    {
        var mgr = CreateManager();
        var session = mgr.Create();

        var body = session.GetBody();
        var para = new Paragraph(new Run(new Text("Hello") { Space = SpaceProcessingModeValues.Preserve }));
        var delRun = new DeletedRun
        {
            Id = "200",
            Author = "TestAuthor",
            Date = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc)
        };
        delRun.AppendChild(new Run(new DeletedText(" world") { Space = SpaceProcessingModeValues.Preserve }));
        para.AppendChild(delRun);
        body.AppendChild(para);

        TestHelpers.PersistBaseline(mgr, session);

        return (mgr, session.Id, 200);
    }

    // --- track_changes_enable ---

    [Fact]
    public void TrackChanges_Enable()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = RevisionTools.TrackChangesEnable(mgr, CreateSyncManager(), id, enabled: true);
        Assert.Contains("Track Changes enabled", result);

        var doc = mgr.Get(id).Document;
        Assert.True(RevisionHelper.IsTrackChangesEnabled(doc));
    }

    [Fact]
    public void TrackChanges_Disable()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        RevisionTools.TrackChangesEnable(mgr, CreateSyncManager(), id, enabled: true);
        var result = RevisionTools.TrackChangesEnable(mgr, CreateSyncManager(), id, enabled: false);
        Assert.Contains("disabled", result);

        var doc = mgr.Get(id).Document;
        Assert.False(RevisionHelper.IsTrackChangesEnabled(doc));
    }

    [Fact]
    public void TrackChanges_EnableTwice_Idempotent()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        RevisionTools.TrackChangesEnable(mgr, CreateSyncManager(), id, enabled: true);
        RevisionTools.TrackChangesEnable(mgr, CreateSyncManager(), id, enabled: true);

        var doc = mgr.Get(id).Document;
        Assert.True(RevisionHelper.IsTrackChangesEnabled(doc));
    }

    // --- revision_list ---

    [Fact]
    public void RevisionList_Empty()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = RevisionTools.RevisionList(mgr, id);
        var json = JsonDocument.Parse(result).RootElement;

        Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Equal(0, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public void RevisionList_WithInsertions()
    {
        var (mgr, id, _) = CreateSessionWithInsertion();

        var result = RevisionTools.RevisionList(mgr, id);
        var json = JsonDocument.Parse(result).RootElement;

        Assert.True(json.GetProperty("total").GetInt32() >= 1);
        var revisions = json.GetProperty("revisions");
        var firstRev = revisions[0];
        Assert.Equal("insertion", firstRev.GetProperty("type").GetString());
        Assert.Equal("TestAuthor", firstRev.GetProperty("author").GetString());
        Assert.Contains("world", firstRev.GetProperty("content").GetString()!);
    }

    [Fact]
    public void RevisionList_FilterByAuthor()
    {
        var (mgr, id, _) = CreateSessionWithInsertion();

        // Filter by matching author
        var result = RevisionTools.RevisionList(mgr, id, author: "TestAuthor");
        var json = JsonDocument.Parse(result).RootElement;
        Assert.True(json.GetProperty("total").GetInt32() >= 1);

        // Filter by non-matching author
        var result2 = RevisionTools.RevisionList(mgr, id, author: "Nobody");
        var json2 = JsonDocument.Parse(result2).RootElement;
        Assert.Equal(0, json2.GetProperty("total").GetInt32());
    }

    [Fact]
    public void RevisionList_FilterByType()
    {
        var (mgr, id, _) = CreateSessionWithInsertion();

        var result = RevisionTools.RevisionList(mgr, id, type: "insertion");
        var json = JsonDocument.Parse(result).RootElement;
        Assert.True(json.GetProperty("total").GetInt32() >= 1);

        var result2 = RevisionTools.RevisionList(mgr, id, type: "deletion");
        var json2 = JsonDocument.Parse(result2).RootElement;
        Assert.Equal(0, json2.GetProperty("total").GetInt32());
    }

    [Fact]
    public void RevisionList_Pagination()
    {
        var mgr = CreateManager();
        var session = mgr.Create();

        var body = session.GetBody();
        var para = new Paragraph(new Run(new Text("Base") { Space = SpaceProcessingModeValues.Preserve }));

        for (int i = 0; i < 5; i++)
        {
            var insRun = new InsertedRun
            {
                Id = (100 + i).ToString(),
                Author = "TestAuthor",
                Date = DateTime.UtcNow
            };
            insRun.AppendChild(new Run(new Text($" word{i}") { Space = SpaceProcessingModeValues.Preserve }));
            para.AppendChild(insRun);
        }
        body.AppendChild(para);

        TestHelpers.PersistBaseline(mgr, session);

        var result = RevisionTools.RevisionList(mgr, session.Id, offset: 2, limit: 2);
        var json = JsonDocument.Parse(result).RootElement;

        Assert.Equal(5, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        Assert.Equal(2, json.GetProperty("offset").GetInt32());
    }

    // --- revision_accept ---

    [Fact]
    public void RevisionAccept_InsertedRun()
    {
        var (mgr, id, revId) = CreateSessionWithInsertion();

        var result = RevisionTools.RevisionAccept(mgr, CreateSyncManager(), id, revId);
        Assert.Contains("Accepted", result);

        // After accepting, InsertedRun should be gone but content remains
        var doc = mgr.Get(id).Document;
        var body = doc.MainDocumentPart!.Document!.Body!;
        Assert.Empty(body.Descendants<InsertedRun>());

        // Content should still be there
        var paraText = body.Elements<Paragraph>().First().InnerText;
        Assert.Contains("world", paraText);
    }

    [Fact]
    public void RevisionAccept_DeletedRun()
    {
        var (mgr, id, revId) = CreateSessionWithDeletion();

        var result = RevisionTools.RevisionAccept(mgr, CreateSyncManager(), id, revId);
        Assert.Contains("Accepted", result);

        // After accepting deletion, both DeletedRun and its content should be gone
        var doc = mgr.Get(id).Document;
        var body = doc.MainDocumentPart!.Document!.Body!;
        Assert.Empty(body.Descendants<DeletedRun>());
    }

    [Fact]
    public void RevisionAccept_NotFound_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = RevisionTools.RevisionAccept(mgr, CreateSyncManager(), id, 999);
        Assert.Contains("Error", result);
        Assert.Contains("not found", result);
    }

    // --- revision_reject ---

    [Fact]
    public void RevisionReject_InsertedRun()
    {
        var (mgr, id, revId) = CreateSessionWithInsertion();

        var result = RevisionTools.RevisionReject(mgr, CreateSyncManager(), id, revId);
        Assert.Contains("Rejected", result);

        // After rejecting insertion, InsertedRun AND its content should be gone
        var doc = mgr.Get(id).Document;
        var body = doc.MainDocumentPart!.Document!.Body!;
        Assert.Empty(body.Descendants<InsertedRun>());
        var paraText = body.Elements<Paragraph>().First().InnerText;
        Assert.DoesNotContain("world", paraText);
    }

    [Fact]
    public void RevisionReject_DeletedRun()
    {
        var (mgr, id, revId) = CreateSessionWithDeletion();

        var result = RevisionTools.RevisionReject(mgr, CreateSyncManager(), id, revId);
        Assert.Contains("Rejected", result);

        // After rejecting deletion, content should be restored as normal text
        var doc = mgr.Get(id).Document;
        var body = doc.MainDocumentPart!.Document!.Body!;
        Assert.Empty(body.Descendants<DeletedRun>());

        // The deleted text should be back as normal text
        var paraText = body.Elements<Paragraph>().First().InnerText;
        Assert.Contains("world", paraText);
    }

    [Fact]
    public void RevisionReject_NotFound_ReturnsError()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = RevisionTools.RevisionReject(mgr, CreateSyncManager(), id, 999);
        Assert.Contains("Error", result);
        Assert.Contains("not found", result);
    }

    // --- WAL persistence (survives restart) ---

    [Fact]
    public void Accept_SurvivesRestart()
    {
        var tenantId = $"test-rev-accept-{Guid.NewGuid():N}";
        var mgr = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr.Create();
        var id = session.Id;

        // Build baseline with tracked insertion
        var body = session.GetBody();
        var para = new Paragraph(new Run(new Text("Hello") { Space = SpaceProcessingModeValues.Preserve }));
        var insRun = new InsertedRun { Id = "100", Author = "Test", Date = DateTime.UtcNow };
        insRun.AppendChild(new Run(new Text(" world") { Space = SpaceProcessingModeValues.Preserve }));
        para.AppendChild(insRun);
        body.AppendChild(para);
        TestHelpers.PersistBaseline(mgr, session);

        // Accept via tool (appends WAL)
        RevisionTools.RevisionAccept(mgr, CreateSyncManager(), id, 100);

        // Simulate restart
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        var doc2 = mgr2.Get(id).Document;
        var body2 = doc2.MainDocumentPart!.Document!.Body!;

        // InsertedRun should be gone after WAL replay
        Assert.Empty(body2.Descendants<InsertedRun>());
    }

    [Fact]
    public void Reject_SurvivesRestart()
    {
        var tenantId = $"test-rev-reject-{Guid.NewGuid():N}";
        var mgr = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr.Create();
        var id = session.Id;

        // Build baseline with tracked insertion
        var body = session.GetBody();
        var para = new Paragraph(new Run(new Text("Hello") { Space = SpaceProcessingModeValues.Preserve }));
        var insRun = new InsertedRun { Id = "100", Author = "Test", Date = DateTime.UtcNow };
        insRun.AppendChild(new Run(new Text(" world") { Space = SpaceProcessingModeValues.Preserve }));
        para.AppendChild(insRun);
        body.AppendChild(para);
        TestHelpers.PersistBaseline(mgr, session);

        // Reject via tool (appends WAL)
        RevisionTools.RevisionReject(mgr, CreateSyncManager(), id, 100);

        // Simulate restart
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        var doc2 = mgr2.Get(id).Document;
        var body2 = doc2.MainDocumentPart!.Document!.Body!;

        Assert.Empty(body2.Descendants<InsertedRun>());
        // Rejected insertion means text is removed
        var paraText = body2.Elements<Paragraph>().First().InnerText;
        Assert.DoesNotContain("world", paraText);
    }

    [Fact]
    public void TrackChanges_SurvivesRestart()
    {
        var tenantId = $"test-rev-track-{Guid.NewGuid():N}";
        var mgr = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr.Create();
        var id = session.Id;

        RevisionTools.TrackChangesEnable(mgr, CreateSyncManager(), id, enabled: true);

        // Simulate restart
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        var doc2 = mgr2.Get(id).Document;
        Assert.True(RevisionHelper.IsTrackChangesEnabled(doc2));
    }

    // --- Undo/Redo ---

    [Fact]
    public void Accept_Undo()
    {
        var (mgr, id, revId) = CreateSessionWithInsertion();

        // Accept the revision
        RevisionTools.RevisionAccept(mgr, CreateSyncManager(), id, revId);

        // Verify InsertedRun is gone
        var doc1 = mgr.Get(id).Document;
        Assert.Empty(doc1.MainDocumentPart!.Document!.Body!.Descendants<InsertedRun>());

        // Undo should restore the InsertedRun
        mgr.Undo(id);

        var doc2 = mgr.Get(id).Document;
        var insRuns = doc2.MainDocumentPart!.Document!.Body!.Descendants<InsertedRun>().ToList();
        Assert.NotEmpty(insRuns);
    }

    [Fact]
    public void Reject_Undo()
    {
        var (mgr, id, revId) = CreateSessionWithInsertion();

        // Reject the insertion (removes the inserted content)
        RevisionTools.RevisionReject(mgr, CreateSyncManager(), id, revId);

        var doc1 = mgr.Get(id).Document;
        Assert.DoesNotContain("world", doc1.MainDocumentPart!.Document!.Body!.InnerText);

        // Undo should restore the InsertedRun with content
        mgr.Undo(id);

        var doc2 = mgr.Get(id).Document;
        Assert.Contains("world", doc2.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public void Accept_Undo_Redo()
    {
        var (mgr, id, revId) = CreateSessionWithInsertion();

        // Accept
        RevisionTools.RevisionAccept(mgr, CreateSyncManager(), id, revId);
        Assert.Empty(mgr.Get(id).Document.MainDocumentPart!.Document!.Body!.Descendants<InsertedRun>());

        // Undo
        mgr.Undo(id);
        Assert.NotEmpty(mgr.Get(id).Document.MainDocumentPart!.Document!.Body!.Descendants<InsertedRun>());

        // Redo
        mgr.Redo(id);
        Assert.Empty(mgr.Get(id).Document.MainDocumentPart!.Document!.Body!.Descendants<InsertedRun>());
        Assert.Contains("world", mgr.Get(id).Document.MainDocumentPart!.Document!.Body!.InnerText);
    }
}
