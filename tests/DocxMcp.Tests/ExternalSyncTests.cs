using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Diff;
using DocxMcp.ExternalChanges;
using DocxMcp.Grpc;
using DocxMcp.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests for external sync WAL integration.
/// </summary>
public class ExternalSyncTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<DocxSession> _sessions = [];
    private readonly SessionManager _sessionManager;
    private readonly ExternalChangeTracker _tracker;

    public ExternalSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"docx-mcp-sync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _sessionManager = TestHelpers.CreateSessionManager();
        _tracker = new ExternalChangeTracker(_sessionManager, NullLogger<ExternalChangeTracker>.Instance);
        _sessionManager.SetExternalChangeTracker(_tracker);
    }

    #region SyncExternalChanges Tests

    [Fact]
    public void SyncExternalChanges_WhenNoChanges_ReturnsNoChanges()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);

        // Save the session back to disk to ensure file hash matches
        // (opening a session assigns IDs which changes the bytes)
        _sessionManager.Save(session.Id, filePath);

        // Act
        var result = _tracker.SyncExternalChanges(session.Id);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.HasChanges);
        Assert.Contains("No external changes", result.Message);
    }

    [Fact]
    public void SyncExternalChanges_WhenFileModified_SyncsAndRecordsInWal()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);
        _tracker.StartWatching(session.Id);

        // Modify the file externally
        ModifyDocx(filePath, "Modified content");

        // Act
        var result = _tracker.SyncExternalChanges(session.Id);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.HasChanges);
        Assert.NotNull(result.Summary);
        Assert.NotNull(result.WalPosition);
        Assert.True(result.WalPosition > 0);
    }

    [Fact]
    public void SyncExternalChanges_CreatesCheckpoint()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);

        ModifyDocx(filePath, "Changed");

        // Act
        var result = _tracker.SyncExternalChanges(session.Id);

        // Assert - checkpoint is created at the WAL position
        Assert.NotNull(result.WalPosition);
        Assert.True(result.WalPosition > 0, "Checkpoint should be created for sync");

        // Verify checkpoint exists by checking that we can jump to that position
        var history = _sessionManager.GetHistory(session.Id);
        var syncEntry = history.Entries.FirstOrDefault(e => e.IsExternalSync);
        Assert.NotNull(syncEntry);
    }

    [Fact]
    public void SyncExternalChanges_RecordsExternalSyncEntryType()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);

        ModifyDocx(filePath, "Changed");
        _tracker.SyncExternalChanges(session.Id);

        // Act
        var history = _sessionManager.GetHistory(session.Id);

        // Assert
        var syncEntry = history.Entries.FirstOrDefault(e => e.IsExternalSync);
        Assert.NotNull(syncEntry);
        Assert.NotNull(syncEntry.SyncSummary);
    }

    [Fact]
    public void SyncExternalChanges_AcknowledgesChangeIdIfProvided()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);
        _tracker.StartWatching(session.Id);

        ModifyDocx(filePath, "Changed");
        var patch = _tracker.CheckForChanges(session.Id)!;

        // Act
        var result = _tracker.SyncExternalChanges(session.Id, patch.Id);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(patch.Id, result.AcknowledgedChangeId);
        Assert.False(_tracker.HasPendingChanges(session.Id));
    }

    [Fact]
    public void SyncExternalChanges_ReloadsDocumentFromDisk()
    {
        // Arrange
        var filePath = CreateTempDocx("Original paragraph");
        var session = OpenSession(filePath);

        // Get original text
        var originalText = GetFirstParagraphText(session);

        // Modify externally
        ModifyDocx(filePath, "Externally modified paragraph");

        // Act
        _tracker.SyncExternalChanges(session.Id);

        // Assert - session should now have the new content
        var updatedSession = _sessionManager.Get(session.Id);
        var updatedText = GetFirstParagraphText(updatedSession);
        Assert.Contains("Externally modified", updatedText);
        Assert.DoesNotContain("Original", updatedText);
    }

    #endregion

    #region Undo/Redo with External Sync Tests

    [Fact]
    public void Undo_AfterExternalSync_RestoresPreSyncState()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);

        // Get initial state
        var initialText = GetFirstParagraphText(_sessionManager.Get(session.Id));

        // Modify and sync
        ModifyDocx(filePath, "Synced content");
        _tracker.SyncExternalChanges(session.Id);

        // Verify sync worked
        var syncedText = GetFirstParagraphText(_sessionManager.Get(session.Id));
        Assert.Contains("Synced", syncedText);

        // Act - undo the sync
        var undoResult = _sessionManager.Undo(session.Id);

        // Assert
        Assert.True(undoResult.Steps > 0);
        var restoredText = GetFirstParagraphText(_sessionManager.Get(session.Id));
        Assert.Contains("Original", restoredText);
    }

    [Fact]
    public void Redo_AfterUndoingExternalSync_ReappliesSyncedState()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);

        ModifyDocx(filePath, "Synced content here");
        var syncResult = _tracker.SyncExternalChanges(session.Id);
        Assert.True(syncResult.HasChanges, "Sync should detect changes");

        // Get synced state text
        var syncedText = GetFirstParagraphText(_sessionManager.Get(session.Id));
        Assert.Contains("Synced", syncedText);

        // Undo the sync
        var undoResult = _sessionManager.Undo(session.Id);
        Assert.True(undoResult.Steps > 0, "Undo should work");

        // Act - Redo should use checkpoint for external sync entries
        var redoResult = _sessionManager.Redo(session.Id);

        // Assert
        Assert.True(redoResult.Steps > 0, "Redo should work");
        var text = GetFirstParagraphText(_sessionManager.Get(session.Id));
        Assert.Contains("Synced", text);
    }

    [Fact]
    public void JumpTo_ExternalSyncPosition_LoadsFromCheckpoint()
    {
        // Arrange
        var filePath = CreateTempDocx("Initial");
        var session = OpenSession(filePath);

        // Make a regular change
        var body = _sessionManager.Get(session.Id).GetBody();
        var newPara = new Paragraph(new Run(new Text("Regular change")));
        body.AppendChild(newPara);
        _sessionManager.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/paragraph[-1]\",\"value\":{\"type\":\"paragraph\"}}]");

        // External sync
        ModifyDocx(filePath, "External sync content");
        var syncResult = _tracker.SyncExternalChanges(session.Id);
        var syncPosition = syncResult.WalPosition!.Value;

        // Make another change after sync
        body = _sessionManager.Get(session.Id).GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("After sync"))));
        _sessionManager.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/paragraph[-1]\",\"value\":{\"type\":\"paragraph\"}}]");

        // Act - jump back to sync position
        _sessionManager.JumpTo(session.Id, syncPosition);

        // Assert - should be at the synced state
        var text = GetFirstParagraphText(_sessionManager.Get(session.Id));
        Assert.Contains("External sync", text);
    }

    #endregion

    #region Uncovered Change Detection Tests

    [Fact]
    public void DetectUncoveredChanges_DetectsHeaderModification()
    {
        // Arrange
        var filePath1 = CreateTempDocx("Content");
        var filePath2 = CreateTempDocxWithHeader("Content", "My Header");

        using var doc1 = WordprocessingDocument.Open(filePath1, false);
        using var doc2 = WordprocessingDocument.Open(filePath2, false);

        // Act
        var uncovered = DiffEngine.DetectUncoveredChanges(doc1, doc2);

        // Assert
        Assert.Contains(uncovered, u => u.Type == UncoveredChangeType.Header);
    }

    [Fact]
    public void DetectUncoveredChanges_DetectsStyleModification()
    {
        // Arrange
        var filePath1 = CreateTempDocx("Content");
        var filePath2 = CreateTempDocxWithCustomStyle("Content", "CustomHeading");

        using var doc1 = WordprocessingDocument.Open(filePath1, false);
        using var doc2 = WordprocessingDocument.Open(filePath2, false);

        // Act
        var uncovered = DiffEngine.DetectUncoveredChanges(doc1, doc2);

        // Assert
        Assert.Contains(uncovered, u => u.Type == UncoveredChangeType.StyleDefinition);
    }

    [Fact]
    public void SyncExternalChanges_IncludesUncoveredChanges()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);

        // Modify with header (uncovered change)
        CreateTempDocxWithHeader("Modified", "New Header", filePath);

        // Act
        var result = _tracker.SyncExternalChanges(session.Id);

        // Assert
        Assert.True(result.HasChanges);
        Assert.NotNull(result.UncoveredChanges);
        // Note: The original doc doesn't have a header, so adding one should be detected
    }

    #endregion

    #region History Display Tests

    [Fact]
    public void GetHistory_ShowsExternalSyncEntriesDistinctly()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);

        // Regular change
        var body = _sessionManager.Get(session.Id).GetBody();
        body.AppendChild(new Paragraph(new Run(new Text("Regular"))));
        _sessionManager.AppendWal(session.Id, "[{\"op\":\"add\",\"path\":\"/body/paragraph[-1]\",\"value\":{\"type\":\"paragraph\"}}]");

        // External sync
        ModifyDocx(filePath, "External");
        _tracker.SyncExternalChanges(session.Id);

        // Act
        var history = _sessionManager.GetHistory(session.Id);

        // Assert
        var entries = history.Entries;
        Assert.Equal(3, entries.Count); // baseline + regular + sync

        var syncEntry = entries.FirstOrDefault(e => e.IsExternalSync);
        Assert.NotNull(syncEntry);
        Assert.NotNull(syncEntry.SyncSummary);
        Assert.NotEmpty(syncEntry.SyncSummary.SourcePath);
    }

    [Fact]
    public void ExternalSyncSummary_ContainsExpectedFields()
    {
        // Arrange
        var filePath = CreateTempDocx("Line 1");
        var session = OpenSession(filePath);

        ModifyDocxMultipleParagraphs(filePath, new[] { "New 1", "New 2", "New 3" });
        _tracker.SyncExternalChanges(session.Id);

        // Act
        var history = _sessionManager.GetHistory(session.Id);
        var syncEntry = history.Entries.First(e => e.IsExternalSync);

        // Assert
        Assert.NotNull(syncEntry.SyncSummary);
        Assert.True(syncEntry.SyncSummary.Added >= 0);
        Assert.True(syncEntry.SyncSummary.Removed >= 0);
        Assert.True(syncEntry.SyncSummary.Modified >= 0);
    }

    #endregion

    #region WAL Entry Serialization Tests

    [Fact]
    public void WalEntry_ExternalSync_SerializesAndDeserializesCorrectly()
    {
        // Arrange
        var entry = new DocxMcp.Persistence.WalEntry
        {
            EntryType = WalEntryType.ExternalSync,
            Timestamp = DateTime.UtcNow,
            Description = "[EXTERNAL SYNC] +1 -0 ~2",
            Patches = "[]",
            SyncMeta = new ExternalSyncMeta
            {
                SourcePath = "/path/to/file.docx",
                PreviousHash = "abc123",
                NewHash = "def456",
                Summary = new DiffSummary { TotalChanges = 3, Added = 1, Removed = 0, Modified = 2, Moved = 0 },
                UncoveredChanges = [
                    new UncoveredChange { Type = UncoveredChangeType.Header, Description = "Header modified", ChangeKind = "modified" }
                ],
                DocumentSnapshot = new byte[] { 0x50, 0x4B, 0x03, 0x04 } // DOCX magic bytes
            }
        };

        // Act
        var json = System.Text.Json.JsonSerializer.Serialize(entry, WalJsonContext.Default.WalEntry);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize(json, WalJsonContext.Default.WalEntry);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(WalEntryType.ExternalSync, deserialized.EntryType);
        Assert.NotNull(deserialized.SyncMeta);
        Assert.Equal("/path/to/file.docx", deserialized.SyncMeta.SourcePath);
        Assert.Equal("abc123", deserialized.SyncMeta.PreviousHash);
        Assert.Single(deserialized.SyncMeta.UncoveredChanges);
        Assert.Equal(UncoveredChangeType.Header, deserialized.SyncMeta.UncoveredChanges[0].Type);
    }

    #endregion

    #region Helpers

    private string CreateTempDocx(string content)
    {
        var filePath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.docx");

        using var session = DocxSession.Create();
        var body = session.GetBody();
        var para = new Paragraph();
        var run = new Run();
        run.AppendChild(new Text(content) { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(run);
        body.AppendChild(para);
        session.Save(filePath);

        return filePath;
    }

    private string CreateTempDocxWithHeader(string bodyContent, string headerContent, string? outputPath = null)
    {
        var filePath = outputPath ?? Path.Combine(_tempDir, $"{Guid.NewGuid():N}.docx");

        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            new Paragraph(new Run(new Text(bodyContent)))
        ));

        // Add header
        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(
            new Paragraph(new Run(new Text(headerContent)))
        );
        headerPart.Header.Save();

        // Reference header in section properties
        var sectPr = mainPart.Document.Body!.GetFirstChild<SectionProperties>()
            ?? mainPart.Document.Body.AppendChild(new SectionProperties());
        sectPr.AppendChild(new HeaderReference
        {
            Type = HeaderFooterValues.Default,
            Id = mainPart.GetIdOfPart(headerPart)
        });

        doc.Save();
        ms.Position = 0;
        File.WriteAllBytes(filePath, ms.ToArray());

        return filePath;
    }

    private string CreateTempDocxWithCustomStyle(string content, string styleName)
    {
        var filePath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.docx");

        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            new Paragraph(new Run(new Text(content)))
        ));

        // Add custom style
        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            new Style(
                new StyleName { Val = styleName },
                new PrimaryStyle()
            )
            { Type = StyleValues.Paragraph, StyleId = styleName }
        );
        stylesPart.Styles.Save();

        doc.Save();
        ms.Position = 0;
        File.WriteAllBytes(filePath, ms.ToArray());

        return filePath;
    }

    private void ModifyDocx(string filePath, string newContent)
    {
        Thread.Sleep(100); // Ensure different timestamp

        using var session = DocxSession.Open(filePath);
        var body = session.GetBody();

        foreach (var child in body.ChildElements.ToList())
        {
            if (child is Paragraph)
                body.RemoveChild(child);
        }

        var para = new Paragraph();
        var run = new Run();
        run.AppendChild(new Text(newContent) { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(run);
        body.AppendChild(para);

        session.Save(filePath);
    }

    private void ModifyDocxMultipleParagraphs(string filePath, string[] paragraphs)
    {
        Thread.Sleep(100);

        using var session = DocxSession.Open(filePath);
        var body = session.GetBody();

        foreach (var child in body.ChildElements.ToList())
        {
            if (child is Paragraph)
                body.RemoveChild(child);
        }

        foreach (var text in paragraphs)
        {
            var para = new Paragraph();
            var run = new Run();
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(run);
            body.AppendChild(para);
        }

        session.Save(filePath);
    }

    private DocxSession OpenSession(string filePath)
    {
        var session = _sessionManager.Open(filePath);
        _sessions.Add(session);
        return session;
    }

    private static string GetFirstParagraphText(DocxSession session)
    {
        var para = session.GetBody().Elements<Paragraph>().FirstOrDefault();
        return para?.InnerText ?? "";
    }

    #endregion

    public void Dispose()
    {
        _tracker.Dispose();

        foreach (var session in _sessions)
        {
            try { _sessionManager.Close(session.Id); }
            catch { /* ignore */ }
        }

        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); }
            catch { /* ignore */ }
        }
    }
}
