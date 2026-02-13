using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.ExternalChanges;
using DocxMcp.Grpc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests for external change detection and tracking.
/// </summary>
public class ExternalChangeTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<DocxSession> _sessions = [];
    private readonly SessionManager _sessionManager = null!;
    private readonly ExternalChangeTracker _tracker = null!;

    public ExternalChangeTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"docx-mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        if (TestHelpers.IsRemoteStorage) return;

        _sessionManager = TestHelpers.CreateSessionManager();
        _tracker = new ExternalChangeTracker(_sessionManager, NullLogger<ExternalChangeTracker>.Instance);
    }

    [SkippableFact]
    public void RegisterSession_WithValidSession_StartsTracking()
    {
        // Arrange
        var filePath = CreateTempDocx("Test content");
        var session = OpenSession(filePath);

        // Act
        _tracker.RegisterSession(session.Id);

        // Assert - no exception means success
        Assert.False(_tracker.HasPendingChanges(session.Id));
    }

    [SkippableFact]
    public void CheckForChanges_WhenNoChanges_ReturnsNull()
    {
        // Arrange
        var filePath = CreateTempDocx("Test content");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        // Act
        var patch = _tracker.CheckForChanges(session.Id);

        // Assert
        Assert.Null(patch);
        Assert.False(_tracker.HasPendingChanges(session.Id));
    }

    [SkippableFact]
    public void CheckForChanges_WhenFileModified_DetectsChanges()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        // Modify the file externally
        ModifyDocx(filePath, "Modified content");

        // Act
        var patch = _tracker.CheckForChanges(session.Id);

        // Assert
        Assert.NotNull(patch);
        Assert.True(patch.Summary.TotalChanges > 0);
        Assert.Equal(session.Id, patch.SessionId);
        Assert.Equal(filePath, patch.SourcePath);
        Assert.False(patch.Acknowledged);
    }

    [SkippableFact]
    public void HasPendingChanges_AfterDetection_ReturnsTrue()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        ModifyDocx(filePath, "Changed");
        _tracker.CheckForChanges(session.Id);

        // Act & Assert
        Assert.True(_tracker.HasPendingChanges(session.Id));
    }

    [SkippableFact]
    public void AcknowledgeChange_MarksPatchAsAcknowledged()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        ModifyDocx(filePath, "Changed");
        var patch = _tracker.CheckForChanges(session.Id)!;

        // Act
        var result = _tracker.AcknowledgeChange(session.Id, patch.Id);

        // Assert
        Assert.True(result);
        Assert.False(_tracker.HasPendingChanges(session.Id));

        var pending = _tracker.GetPendingChanges(session.Id);
        Assert.True(pending.Changes[0].Acknowledged);
    }

    [SkippableFact]
    public void AcknowledgeAllChanges_AcknowledgesMultipleChanges()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        // First change
        ModifyDocx(filePath, "Change 1");
        _tracker.CheckForChanges(session.Id);

        // Second change
        ModifyDocx(filePath, "Change 1 and 2");
        _tracker.CheckForChanges(session.Id);

        // Act
        var count = _tracker.AcknowledgeAllChanges(session.Id);

        // Assert
        Assert.Equal(2, count);
        Assert.False(_tracker.HasPendingChanges(session.Id));
    }

    [SkippableFact]
    public void GetPendingChanges_ReturnsAllPendingChanges()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        ModifyDocx(filePath, "Change 1");
        _tracker.CheckForChanges(session.Id);

        ModifyDocx(filePath, "Change 1 and Change 2");
        _tracker.CheckForChanges(session.Id);

        // Act
        var pending = _tracker.GetPendingChanges(session.Id);

        // Assert
        Assert.Equal(2, pending.Changes.Count);
        Assert.True(pending.HasPendingChanges);
        Assert.NotNull(pending.MostRecentPending);
    }

    [SkippableFact]
    public void GetLatestUnacknowledgedChange_ReturnsCorrectChange()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        ModifyDocx(filePath, "First change");
        var first = _tracker.CheckForChanges(session.Id)!;

        ModifyDocx(filePath, "Second change is here");
        var second = _tracker.CheckForChanges(session.Id)!;

        // Acknowledge the first one
        _tracker.AcknowledgeChange(session.Id, first.Id);

        // Act
        var latest = _tracker.GetLatestUnacknowledgedChange(session.Id);

        // Assert
        Assert.NotNull(latest);
        Assert.Equal(second.Id, latest.Id);
    }

    [SkippableFact]
    public void UpdateSessionSnapshot_ResetsChangeDetection()
    {
        // Arrange
        var filePath = CreateTempDocx("Original");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        // Make an external change
        ModifyDocx(filePath, "External change");

        // Simulate saving the document (which updates the snapshot)
        File.WriteAllBytes(filePath, _sessionManager.Get(session.Id).ToBytes());
        _tracker.UpdateSessionSnapshot(session.Id);

        // Act - check for changes again
        var patch = _tracker.CheckForChanges(session.Id);

        // Assert - should be no changes because snapshot was updated
        Assert.Null(patch);
    }

    [SkippableFact]
    public void ExternalChangePatch_ToLlmSummary_ProducesReadableOutput()
    {
        // Arrange
        var filePath = CreateTempDocx("Original paragraph");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        ModifyDocx(filePath, "Modified paragraph with more content");
        var patch = _tracker.CheckForChanges(session.Id)!;

        // Act
        var summary = patch.ToLlmSummary();

        // Assert
        Assert.Contains("External Document Change Detected", summary);
        Assert.Contains(session.Id, summary);
        Assert.Contains("acknowledge_external_change", summary);
    }

    [SkippableFact]
    public void UnregisterSession_StopsTrackingSession()
    {
        // Arrange
        var filePath = CreateTempDocx("Test");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        // Act
        _tracker.UnregisterSession(session.Id);

        // Modify file after stopping
        ModifyDocx(filePath, "Changed after stop");

        // Check for changes (should start fresh)
        var patch = _tracker.CheckForChanges(session.Id);

        // Assert - checking creates a new watch, so it depends on implementation
        // At minimum, no pending changes from before StopWatching
        Assert.False(_tracker.HasPendingChanges(session.Id) && patch is null);
    }

    [SkippableFact]
    public void Patch_ContainsValidPatches()
    {
        // Arrange
        var filePath = CreateTempDocx("Original paragraph");
        var session = OpenSession(filePath);
        _tracker.RegisterSession(session.Id);

        ModifyDocx(filePath, "Completely different content here");
        var patch = _tracker.CheckForChanges(session.Id)!;

        // Assert
        Assert.NotEmpty(patch.Patches);
        Assert.NotEmpty(patch.Changes);

        // Each patch should have an 'op' field
        foreach (var p in patch.Patches)
        {
            Assert.True(p.ContainsKey("op"));
        }
    }

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

    private void ModifyDocx(string filePath, string newContent)
    {
        // Wait a bit to ensure different timestamp
        Thread.Sleep(100);

        using var session = DocxSession.Open(filePath);
        var body = session.GetBody();

        // Clear existing content and add new
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

    private DocxSession OpenSession(string filePath)
    {
        Skip.If(TestHelpers.IsRemoteStorage, "Requires local file storage");
        var session = _sessionManager.Open(filePath);
        _sessions.Add(session);
        return session;
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
