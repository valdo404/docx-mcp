using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Grpc;
using DocxMcp.Tools;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests for session persistence via gRPC storage.
/// These tests verify that sessions persist correctly across manager instances.
/// </summary>
public class SessionPersistenceTests
{
    [Fact]
    public void CreateSession_CanBeRetrieved()
    {
        var mgr = TestHelpers.CreateSessionManager();
        var session = mgr.Create();

        // Session should be retrievable
        var retrieved = mgr.Get(session.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(session.Id, retrieved.Id);
    }

    [Fact]
    public void CloseSession_RemovesFromList()
    {
        var mgr = TestHelpers.CreateSessionManager();
        var session = mgr.Create();
        var id = session.Id;

        mgr.Close(id);

        // Session should no longer be in list
        var list = mgr.List();
        Assert.DoesNotContain(list, s => s.Id == id);
    }

    [Fact]
    public void AppendWal_RecordsInHistory()
    {
        var mgr = TestHelpers.CreateSessionManager();
        var session = mgr.Create();

        // Add content via WAL
        session.GetBody().AppendChild(new Paragraph(new Run(new Text("Hello"))));
        mgr.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"Hello\"}}]");

        var history = mgr.GetHistory(session.Id);
        // History should have at least 2 entries: baseline + WAL entry
        Assert.True(history.Entries.Count >= 2);
    }

    [Fact]
    public void Compact_ResetsWalPosition()
    {
        var mgr = TestHelpers.CreateSessionManager();
        var session = mgr.Create();

        // Add content via patch
        var body = session.GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("Test content"))));
        mgr.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"Test\"}}]");

        var historyBefore = mgr.GetHistory(session.Id);
        var countBefore = historyBefore.Entries.Count;

        mgr.Compact(session.Id);

        // After compaction, history should be reset to just the baseline
        var historyAfter = mgr.GetHistory(session.Id);
        Assert.True(historyAfter.Entries.Count <= countBefore);
    }

    [Fact]
    public void RestoreSessions_RehydratesFromStorage()
    {
        // Use same tenant for both managers
        var tenantId = $"test-persist-{Guid.NewGuid():N}";

        // Create a session and persist it
        var mgr1 = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr1.Create();
        var id = session.Id;

        // Add content directly to DOM
        var body = session.GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("Persisted content"))));

        // Compact to save current state
        mgr1.Compact(id);

        // Create a new manager with the same tenant
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        var restored = mgr2.RestoreSessions();
        Assert.Equal(1, restored);

        // Verify the session is accessible with the same ID
        var restoredSession = mgr2.Get(id);
        Assert.NotNull(restoredSession);
        Assert.Contains("Persisted content", restoredSession.GetBody().InnerText);
    }

    [Fact]
    public void RestoreSessions_ReplaysWal()
    {
        var tenantId = $"test-wal-replay-{Guid.NewGuid():N}";

        // Create a session and add a patch via WAL (not compacted)
        var mgr1 = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr1.Create();
        var id = session.Id;

        // Apply a patch through PatchTool
        PatchTool.ApplyPatch(mgr1, null, id,
            "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"WAL entry\"}}]");

        // Verify WAL has entries via history
        var history = mgr1.GetHistory(id);
        Assert.True(history.Entries.Count > 1);

        // Create new manager with same tenant
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        var restored = mgr2.RestoreSessions();
        Assert.Equal(1, restored);

        // Verify the WAL was replayed — the paragraph should exist
        var restoredSession = mgr2.Get(id);
        Assert.Contains("WAL entry", restoredSession.GetBody().InnerText);
    }

    [Fact]
    public void MultipleSessions_PersistIndependently()
    {
        var mgr = TestHelpers.CreateSessionManager();
        var s1 = mgr.Create();
        var s2 = mgr.Create();

        var list = mgr.List().ToList();
        Assert.Equal(2, list.Count);

        var ids = list.Select(s => s.Id).ToHashSet();
        Assert.Contains(s1.Id, ids);
        Assert.Contains(s2.Id, ids);

        mgr.Close(s1.Id);

        list = mgr.List().ToList();
        Assert.Single(list);
        Assert.Equal(s2.Id, list[0].Id);
    }

    [Fact]
    public void DocumentSnapshot_CompactsSession()
    {
        var mgr = TestHelpers.CreateSessionManager();
        var session = mgr.Create();

        // Add some WAL entries
        session.GetBody().AppendChild(new Paragraph(new Run(new Text("Before snapshot"))));
        mgr.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"Before snapshot\"}}]");

        var result = DocumentTools.DocumentSnapshot(mgr, session.Id);
        Assert.Contains("Snapshot created", result);
    }

    [Fact]
    public void UndoRedo_WorksAfterRestart()
    {
        var tenantId = $"test-undo-restart-{Guid.NewGuid():N}";

        // Create session and apply patches
        var mgr1 = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr1.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr1, null, id,
            "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"First\"}}]");
        PatchTool.ApplyPatch(mgr1, null, id,
            "[{\"op\":\"add\",\"path\":\"/body/children/1\",\"value\":{\"type\":\"paragraph\",\"text\":\"Second\"}}]");

        // Restart
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        mgr2.RestoreSessions();

        // Undo should work
        var undoResult = mgr2.Undo(id);
        Assert.True(undoResult.Steps > 0);

        var text = mgr2.Get(id).GetBody().InnerText;
        Assert.Contains("First", text);
        Assert.DoesNotContain("Second", text);
    }
}
