using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.ExternalChanges;
using DocxMcp.Grpc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests that sync-external does not create duplicate WAL entries
/// when called multiple times without actual file changes.
/// This was the bug: ID reassignment caused hash mismatches even when
/// the external file hadn't changed.
/// </summary>
public class SyncDuplicateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;
    private readonly string _tenantId;
    private readonly SessionManager _sessionManager;
    private readonly ExternalChangeTracker _tracker;

    public SyncDuplicateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"docx-mcp-sync-dup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _tempFile = Path.Combine(_tempDir, "test.docx");

        // Create test document
        CreateTestDocx(_tempFile, "Test content");

        _tenantId = $"test-sync-dup-{Guid.NewGuid():N}";
        _sessionManager = TestHelpers.CreateSessionManager(_tenantId);
        _tracker = new ExternalChangeTracker(_sessionManager, NullLogger<ExternalChangeTracker>.Instance);
        _sessionManager.SetExternalChangeTracker(_tracker);
    }

    [Fact]
    public void SyncExternalChanges_CalledTwice_OnlyCreatesOneWalEntry()
    {
        // Arrange
        var session = _sessionManager.Open(_tempFile);

        // Act - first sync (may or may not have changes depending on ID assignment)
        var result1 = _tracker.SyncExternalChanges(session.Id);

        // Act - second sync (should NOT create a new entry)
        var result2 = _tracker.SyncExternalChanges(session.Id);

        // Assert
        Assert.False(result2.HasChanges, "Second sync should report no changes");

        var history = _sessionManager.GetHistory(session.Id);
        var syncEntries = history.Entries.Count(e => e.IsExternalSync);
        Assert.True(syncEntries <= 1, $"Expected at most 1 sync entry, got {syncEntries}");
    }

    [Fact]
    public void SyncExternalChanges_CalledThreeTimes_OnlyCreatesOneWalEntry()
    {
        // Arrange
        var session = _sessionManager.Open(_tempFile);

        // Act
        _tracker.SyncExternalChanges(session.Id);
        var result2 = _tracker.SyncExternalChanges(session.Id);
        var result3 = _tracker.SyncExternalChanges(session.Id);

        // Assert
        Assert.False(result2.HasChanges, "Second sync should report no changes");
        Assert.False(result3.HasChanges, "Third sync should report no changes");
    }

    [Fact]
    public void SyncExternalChanges_AfterFileModified_CreatesNewEntry()
    {
        // Arrange
        var session = _sessionManager.Open(_tempFile);
        _tracker.SyncExternalChanges(session.Id);

        // Modify the external file
        Thread.Sleep(100); // Ensure different timestamp
        ModifyTestDocx(_tempFile, "Modified content");

        // Act
        var result = _tracker.SyncExternalChanges(session.Id);

        // Assert
        Assert.True(result.HasChanges, "Sync after file modification should have changes");
    }

    [Fact]
    public void SyncExternalChanges_AfterModifyThenNoChange_LastSyncHasNoChanges()
    {
        // Arrange
        var session = _sessionManager.Open(_tempFile);

        // First sync (no changes or initial sync)
        _tracker.SyncExternalChanges(session.Id);

        // Modify and sync
        Thread.Sleep(100);
        ModifyTestDocx(_tempFile, "Modified");
        var modifyResult = _tracker.SyncExternalChanges(session.Id);
        Assert.True(modifyResult.HasChanges);

        // Sync again without changes
        var noChangeResult = _tracker.SyncExternalChanges(session.Id);

        // Assert
        Assert.False(noChangeResult.HasChanges, "Sync after modification sync without further changes should have no changes");
    }

    [Fact]
    public void ResolveSession_WithAbsolutePath_ReturnsExistingSession()
    {
        // Arrange
        var session = _sessionManager.Open(_tempFile);
        var absolutePath = Path.GetFullPath(_tempFile);

        // Act
        var resolved = _sessionManager.ResolveSession(absolutePath);

        // Assert
        Assert.Equal(session.Id, resolved.Id);
    }

    [Fact]
    public void ResolveSession_WithSessionId_ReturnsSession()
    {
        // Arrange
        var session = _sessionManager.Open(_tempFile);

        // Act
        var resolved = _sessionManager.ResolveSession(session.Id);

        // Assert
        Assert.Equal(session.Id, resolved.Id);
    }

    [Fact]
    public void ResolveSession_WithNewPath_OpensNewSession()
    {
        // Arrange
        var newFile = Path.Combine(_tempDir, "new.docx");
        CreateTestDocx(newFile, "New file content");

        // Act
        var session = _sessionManager.ResolveSession(newFile);

        // Assert
        Assert.NotNull(session);
        Assert.Equal(Path.GetFullPath(newFile), session.SourcePath);
    }

    [Fact]
    public void ResolveSession_WithSamePath_ReturnsSameSession()
    {
        // Arrange - open via ResolveSession twice
        var session1 = _sessionManager.ResolveSession(_tempFile);
        var session2 = _sessionManager.ResolveSession(_tempFile);

        // Assert
        Assert.Equal(session1.Id, session2.Id);
    }

    [Fact]
    public void Open_WithRelativePath_StoresAbsolutePath()
    {
        // Arrange
        var currentDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);

        try
        {
            // Act
            var session = _sessionManager.Open("test.docx");

            // Assert
            Assert.True(Path.IsPathRooted(session.SourcePath));
            // Verify the path ends with the expected filename and is absolute
            Assert.EndsWith("test.docx", session.SourcePath);
            // The actual directory portion may differ due to symlink resolution on macOS
            // (/var -> /private/var), but the path should still be valid
            Assert.True(File.Exists(session.SourcePath), "Stored path should point to existing file");
        }
        finally
        {
            Directory.SetCurrentDirectory(currentDir);
        }
    }

    [Fact]
    public void ResolveSession_WithNonExistentPath_ThrowsKeyNotFound()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "does-not-exist.docx");

        // Act & Assert
        Assert.Throws<KeyNotFoundException>(() => _sessionManager.ResolveSession(nonExistentPath));
    }

    [Fact]
    public void ResolveSession_WithInvalidSessionId_ThrowsKeyNotFound()
    {
        // Act & Assert
        Assert.Throws<KeyNotFoundException>(() => _sessionManager.ResolveSession("invalid123456"));
    }

    [Fact]
    public void RestoreSessions_WithExternalSyncCheckpoint_RestoresFromCheckpoint()
    {
        // Arrange
        var session = _sessionManager.Open(_tempFile);
        var sessionId = session.Id;

        // Sync external (creates checkpoint with new content)
        Thread.Sleep(100);
        ModifyTestDocx(_tempFile, "New content from external");
        var syncResult = _tracker.SyncExternalChanges(sessionId);
        Assert.True(syncResult.HasChanges, "Sync should detect changes");

        // Verify synced content is in memory
        var syncedText = GetParagraphText(_sessionManager.Get(sessionId));
        Assert.Contains("New content from external", syncedText);

        // Simulate server restart by creating a new SessionManager with same tenant
        var newSessionManager = TestHelpers.CreateSessionManager(_tenantId);
        var newTracker = new ExternalChangeTracker(newSessionManager, NullLogger<ExternalChangeTracker>.Instance);
        newSessionManager.SetExternalChangeTracker(newTracker);

        // Act - restore sessions
        var restoredCount = newSessionManager.RestoreSessions();

        // Assert - should have restored the session with checkpoint content
        Assert.Equal(1, restoredCount);
        var restoredSession = newSessionManager.Get(sessionId);
        var restoredText = GetParagraphText(restoredSession);
        Assert.Contains("New content from external", restoredText);

        // Additional check: syncing again should NOT create a new WAL entry
        var secondSyncResult = newTracker.SyncExternalChanges(sessionId);
        Assert.False(secondSyncResult.HasChanges, "Sync after restore should report no changes");

        // Cleanup the new tracker
        newTracker.Dispose();
    }

    [Fact]
    public void RestoreSessions_ThenSync_NoDuplicateWalEntries()
    {
        // This test specifically targets the original bug:
        // When RestoreSessions loaded from baseline (ignoring ExternalSync checkpoints),
        // subsequent syncs would detect "changes" and create duplicate WAL entries.

        // Arrange
        var session = _sessionManager.Open(_tempFile);
        var sessionId = session.Id;

        // Create external sync entry
        Thread.Sleep(100);
        ModifyTestDocx(_tempFile, "Externally modified content");
        _tracker.SyncExternalChanges(sessionId);

        var historyBefore = _sessionManager.GetHistory(sessionId);
        var syncEntriesBefore = historyBefore.Entries.Count(e => e.IsExternalSync);

        // Simulate server restart with same tenant
        var newSessionManager = TestHelpers.CreateSessionManager(_tenantId);
        var newTracker = new ExternalChangeTracker(newSessionManager, NullLogger<ExternalChangeTracker>.Instance);
        newSessionManager.SetExternalChangeTracker(newTracker);
        newSessionManager.RestoreSessions();

        // Act - sync multiple times after restart
        newTracker.SyncExternalChanges(sessionId);
        newTracker.SyncExternalChanges(sessionId);
        newTracker.SyncExternalChanges(sessionId);

        // Assert - should still have the same number of sync entries
        var historyAfter = newSessionManager.GetHistory(sessionId);
        var syncEntriesAfter = historyAfter.Entries.Count(e => e.IsExternalSync);

        Assert.Equal(syncEntriesBefore, syncEntriesAfter);

        // Cleanup
        newTracker.Dispose();
    }

    #region Helpers

    private static void CreateTestDocx(string path, string content)
    {
        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            new Paragraph(new Run(new Text(content)))
        ));

        doc.Save();
        ms.Position = 0;
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void ModifyTestDocx(string path, string newContent)
    {
        using var doc = WordprocessingDocument.Open(path, true);
        var body = doc.MainDocumentPart!.Document!.Body!;

        // Clear existing paragraphs
        foreach (var para in body.Elements<Paragraph>().ToList())
        {
            body.RemoveChild(para);
        }

        // Add new content
        body.AppendChild(new Paragraph(new Run(new Text(newContent))));
        doc.Save();
    }

    private static string GetParagraphText(DocxSession session)
    {
        var para = session.GetBody().Elements<Paragraph>().FirstOrDefault();
        return para?.InnerText ?? "";
    }

    #endregion

    public void Dispose()
    {
        _tracker.Dispose();

        // Close any open sessions
        foreach (var (id, _) in _sessionManager.List().ToList())
        {
            try { _sessionManager.Close(id); }
            catch { /* ignore */ }
        }

        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); }
            catch { /* ignore */ }
        }
    }
}
