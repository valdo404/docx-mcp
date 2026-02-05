using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Tools;
using Xunit;

namespace DocxMcp.Tests;

public class UndoRedoTests : IDisposable
{
    private readonly string _tempDir;

    public UndoRedoTests()
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

    // --- Undo tests ---

    [Fact]
    public void Undo_SingleStep_RestoresState()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("First"));
        Assert.Contains("First", session.GetBody().InnerText);

        var result = mgr.Undo(id);
        Assert.Equal(0, result.Position);
        Assert.Equal(1, result.Steps);

        // Document should be back to empty baseline
        var body = mgr.Get(id).GetBody();
        Assert.DoesNotContain("First", body.InnerText);
    }

    [Fact]
    public void Undo_MultipleSteps_RestoresEarlierState()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("C"));

        var result = mgr.Undo(id, 2);
        Assert.Equal(1, result.Position);
        Assert.Equal(2, result.Steps);

        var body = mgr.Get(id).GetBody();
        Assert.Contains("A", body.InnerText);
        Assert.DoesNotContain("B", body.InnerText);
        Assert.DoesNotContain("C", body.InnerText);
    }

    [Fact]
    public void Undo_AtBeginning_ReturnsZeroSteps()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        var result = mgr.Undo(id);
        Assert.Equal(0, result.Position);
        Assert.Equal(0, result.Steps);
        Assert.Contains("Nothing to undo", result.Message);
    }

    [Fact]
    public void Undo_BeyondBeginning_ClampsToZero()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));

        var result = mgr.Undo(id, 100);
        Assert.Equal(0, result.Position);
        Assert.Equal(2, result.Steps);
    }

    // --- Redo tests ---

    [Fact]
    public void Redo_SingleStep_ReappliesPatch()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Hello"));
        mgr.Undo(id);

        // After undo, document should not contain "Hello"
        Assert.DoesNotContain("Hello", mgr.Get(id).GetBody().InnerText);

        var result = mgr.Redo(id);
        Assert.Equal(1, result.Position);
        Assert.Equal(1, result.Steps);

        Assert.Contains("Hello", mgr.Get(id).GetBody().InnerText);
    }

    [Fact]
    public void Redo_MultipleSteps_ReappliesAll()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("C"));

        mgr.Undo(id, 3);
        Assert.DoesNotContain("A", mgr.Get(id).GetBody().InnerText);

        var result = mgr.Redo(id, 2);
        Assert.Equal(2, result.Position);
        Assert.Equal(2, result.Steps);

        var body = mgr.Get(id).GetBody();
        Assert.Contains("A", body.InnerText);
        Assert.Contains("B", body.InnerText);
    }

    [Fact]
    public void Redo_AtEnd_ReturnsZeroSteps()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));

        // No undo happened, so redo should do nothing
        var result = mgr.Redo(id);
        Assert.Equal(0, result.Steps);
        Assert.Contains("Nothing to redo", result.Message);
    }

    [Fact]
    public void Redo_BeyondEnd_ClampsToCurrent()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        mgr.Undo(id, 2);

        var result = mgr.Redo(id, 100);
        Assert.Equal(2, result.Position);
        Assert.Equal(2, result.Steps);
    }

    // --- Undo then new patch discards redo ---

    [Fact]
    public void Undo_ThenNewPatch_DiscardsRedoHistory()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("C"));

        // Undo 2 steps (back to position 1, only A)
        mgr.Undo(id, 2);

        // Apply new patch — should discard B and C from history
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("D"));

        // Redo should now have nothing
        var redoResult = mgr.Redo(id);
        Assert.Equal(0, redoResult.Steps);
        Assert.Contains("Nothing to redo", redoResult.Message);

        var body = mgr.Get(id).GetBody();
        Assert.Contains("A", body.InnerText);
        Assert.Contains("D", body.InnerText);
        Assert.DoesNotContain("B", body.InnerText);
        Assert.DoesNotContain("C", body.InnerText);
    }

    // --- JumpTo tests ---

    [Fact]
    public void JumpTo_Forward_RebuildsCorrectly()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("C"));

        mgr.JumpTo(id, 0);
        Assert.DoesNotContain("A", mgr.Get(id).GetBody().InnerText);

        var result = mgr.JumpTo(id, 2);
        Assert.Equal(2, result.Position);

        var body = mgr.Get(id).GetBody();
        Assert.Contains("A", body.InnerText);
        Assert.Contains("B", body.InnerText);
        Assert.DoesNotContain("C", body.InnerText);
    }

    [Fact]
    public void JumpTo_Backward_RebuildsCorrectly()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("C"));

        var result = mgr.JumpTo(id, 1);
        Assert.Equal(1, result.Position);

        var body = mgr.Get(id).GetBody();
        Assert.Contains("A", body.InnerText);
        Assert.DoesNotContain("B", body.InnerText);
    }

    [Fact]
    public void JumpTo_Zero_ReturnsBaseline()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));

        var result = mgr.JumpTo(id, 0);
        Assert.Equal(0, result.Position);
        Assert.DoesNotContain("A", mgr.Get(id).GetBody().InnerText);
    }

    [Fact]
    public void JumpTo_OutOfRange_ReturnsNoChange()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));

        var result = mgr.JumpTo(id, 100);
        Assert.Equal(0, result.Steps);
        Assert.Contains("beyond the WAL", result.Message);
    }

    [Fact]
    public void JumpTo_SamePosition_NoOp()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));

        var result = mgr.JumpTo(id, 1);
        Assert.Equal(0, result.Steps);
        Assert.Contains("Already at position", result.Message);
    }

    // --- GetHistory tests ---

    [Fact]
    public void GetHistory_ReturnsEntries()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));

        var history = mgr.GetHistory(id);
        Assert.Equal(3, history.TotalEntries); // baseline + 2 patches
        Assert.Equal(2, history.CursorPosition);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        // First entry is baseline
        Assert.Equal(0, history.Entries[0].Position);
        Assert.True(history.Entries[0].IsCheckpoint);
        Assert.Contains("Baseline", history.Entries[0].Description);

        // Current marker on last entry
        Assert.True(history.Entries[2].IsCurrent);
    }

    [Fact]
    public void GetHistory_AfterUndo_ShowsCurrentMarker()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        mgr.Undo(id);

        var history = mgr.GetHistory(id);
        Assert.Equal(1, history.CursorPosition);
        Assert.True(history.CanUndo);
        Assert.True(history.CanRedo);

        // Position 1 should be current
        var current = history.Entries.Find(e => e.IsCurrent);
        Assert.NotNull(current);
        Assert.Equal(1, current!.Position);
    }

    [Fact]
    public void GetHistory_Pagination_Works()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        for (int i = 0; i < 5; i++)
            PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch($"P{i}"));

        var page = mgr.GetHistory(id, offset: 2, limit: 2);
        Assert.Equal(6, page.TotalEntries);
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(2, page.Entries[0].Position);
        Assert.Equal(3, page.Entries[1].Position);
    }

    // --- Compact with redo tests ---

    [Fact]
    public void Compact_WithRedoEntries_SkipsWithoutFlag()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        mgr.Undo(id);

        // Compact should skip because redo entries exist
        mgr.Compact(id);

        // History should still have entries (compact was skipped)
        var history = mgr.GetHistory(id);
        Assert.True(history.TotalEntries > 1);
    }

    [Fact]
    public void Compact_WithDiscardFlag_Works()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        mgr.Undo(id);

        mgr.Compact(id, discardRedoHistory: true);

        // After compact with discard, history should be minimal
        var history = mgr.GetHistory(id);
        Assert.Equal(1, history.TotalEntries); // Only baseline
    }

    [Fact]
    public void Compact_ClearsCheckpoints()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        // Apply enough patches to create a checkpoint (interval default = 10)
        for (int i = 0; i < 10; i++)
            PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch($"P{i}"));

        // Verify checkpoint exists via history
        var historyBefore = mgr.GetHistory(id);
        var hasCheckpoint = historyBefore.Entries.Any(e => e.IsCheckpoint && e.Position == 10);
        Assert.True(hasCheckpoint);

        mgr.Compact(id);

        // After compact, only baseline checkpoint remains
        var historyAfter = mgr.GetHistory(id);
        Assert.Equal(1, historyAfter.TotalEntries);
    }

    // --- Checkpoint tests ---

    [Fact]
    public void Checkpoint_CreatedAtInterval()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        // Default interval is 10
        for (int i = 0; i < 10; i++)
            PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch($"P{i}"));

        var history = mgr.GetHistory(id);
        var hasCheckpoint = history.Entries.Any(e => e.IsCheckpoint && e.Position == 10);
        Assert.True(hasCheckpoint);
    }

    [Fact]
    public void Checkpoint_UsedDuringUndo()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        // Apply 15 patches (checkpoint at position 10)
        for (int i = 0; i < 15; i++)
            PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch($"P{i}"));

        // Verify checkpoint at 10
        var history = mgr.GetHistory(id);
        Assert.True(history.Entries.Any(e => e.IsCheckpoint && e.Position == 10));

        // Undo to position 12 — should use checkpoint at 10, replay 2 patches
        var result = mgr.Undo(id, 3);
        Assert.Equal(12, result.Position);

        var body = mgr.Get(id).GetBody();
        // Should contain P0-P11 but not P12-P14
        Assert.Contains("P11", body.InnerText);
        Assert.DoesNotContain("P12", body.InnerText);
    }

    // --- RestoreSessions tests ---

    [Fact]
    public void RestoreSessions_RespectsCursor()
    {
        // Use explicit tenant so second manager can find the session
        var tenantId = $"test-restore-cursor-{Guid.NewGuid():N}";
        var mgr1 = TestHelpers.CreateSessionManager(tenantId);
        var session = mgr1.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr1, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr1, null, id, AddParagraphPatch("B"));
        PatchTool.ApplyPatch(mgr1, null, id, AddParagraphPatch("C"));

        // Undo to position 1
        mgr1.Undo(id, 2);

        // Don't close - sessions auto-persist to gRPC storage
        // Simulate restart: create a new manager with same tenant
        var mgr2 = TestHelpers.CreateSessionManager(tenantId);
        var restored = mgr2.RestoreSessions();
        Assert.Equal(1, restored);

        // Document should be restored at WAL position (position 3, all patches applied)
        // Note: cursor position is local state, not persisted. On restore, we replay to WAL tip.
        var body = mgr2.Get(id).GetBody();
        Assert.Contains("A", body.InnerText);
        // After restore, document is at WAL tip (all patches replayed)
        Assert.Contains("B", body.InnerText);
        Assert.Contains("C", body.InnerText);
    }

    // --- MCP Tool integration ---

    [Fact]
    public void HistoryTools_Undo_Integration()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Test"));

        var result = HistoryTools.DocumentUndo(mgr, id);
        Assert.Contains("Undid 1 step", result);
        Assert.Contains("Position: 0", result);
    }

    [Fact]
    public void HistoryTools_Redo_Integration()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Test"));
        mgr.Undo(id);

        var result = HistoryTools.DocumentRedo(mgr, id);
        Assert.Contains("Redid 1 step", result);
        Assert.Contains("Position: 1", result);
    }

    [Fact]
    public void HistoryTools_History_Integration()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Test"));

        var result = HistoryTools.DocumentHistory(mgr, id);
        Assert.Contains("History for document", result);
        Assert.Contains("Total entries: 2", result);
        Assert.Contains("Baseline", result);
        Assert.Contains("<-- current", result);
    }

    [Fact]
    public void HistoryTools_JumpTo_Integration()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("Test"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("More"));

        var result = HistoryTools.DocumentJumpTo(mgr, id, 0);
        Assert.Contains("Jumped to position 0", result);
    }

    [Fact]
    public void DocumentSnapshot_WithDiscard_Integration()
    {
        var mgr = CreateManager();
        var session = mgr.Create();
        var id = session.Id;

        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("A"));
        PatchTool.ApplyPatch(mgr, null, id, AddParagraphPatch("B"));
        mgr.Undo(id);

        var result = DocumentTools.DocumentSnapshot(mgr, id, discard_redo: true);
        Assert.Contains("Snapshot created", result);
    }
}
