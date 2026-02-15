using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.ExternalChanges;
using DocxMcp.Grpc;
using DocxMcp.Helpers;
using DocxMcp.Tools;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests for external change detection via ExternalChangeTools and ExternalChangeGate.
/// </summary>
public class ExternalChangeTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<DocxSession> _sessions = [];
    private readonly SessionManager _sessionManager;
    private readonly ExternalChangeGate _gate = TestHelpers.CreateExternalChangeGate();

    public ExternalChangeTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"docx-mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _sessionManager = TestHelpers.CreateSessionManager();
    }

    [Fact]
    public void CheckForChanges_WhenNoChanges_ReturnsNoChanges()
    {
        // Arrange
        var filePath = CreateTempDocx("Test content");
        var session = OpenSession(filePath);

        // Save the session back to disk to match (opening assigns IDs)
        File.WriteAllBytes(filePath, _sessionManager.Get(session.Id).ToBytes());

        // Act
        var result = ExternalChangeTools.PerformSync(_sessionManager, session.Id, isImport: false);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public void CheckForChanges_WhenFileModified_DetectsChanges()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);

        // Modify the file externally
        ModifyDocx(filePath, "Modified content");

        // Act
        var result = ExternalChangeTools.PerformSync(_sessionManager, session.Id, isImport: false);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.HasChanges);
        Assert.NotNull(result.Summary);
        Assert.True(result.Summary.TotalChanges > 0);
    }

    [Fact]
    public void PerformSync_WhenNoSourcePath_ReturnsFailure()
    {
        // Arrange — create a new empty session (no source path)
        var session = _sessionManager.Create();
        _sessions.Add(session);

        // Act
        var result = ExternalChangeTools.PerformSync(_sessionManager, session.Id, isImport: false);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("no source path", result.Message);
    }

    [Fact]
    public void PerformSync_WhenSourceFileDeleted_ReturnsFailure()
    {
        // Arrange
        var filePath = CreateTempDocx("Test");
        var session = OpenSession(filePath);
        File.Delete(filePath);

        // Act
        var result = ExternalChangeTools.PerformSync(_sessionManager, session.Id, isImport: false);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public void Patch_ContainsValidPatches()
    {
        // Arrange
        var filePath = CreateTempDocx("Original paragraph");
        var session = OpenSession(filePath);

        ModifyDocx(filePath, "Completely different content here");

        // Act
        var result = ExternalChangeTools.PerformSync(_sessionManager, session.Id, isImport: false);

        // Assert
        Assert.True(result.HasChanges);
        Assert.NotNull(result.Patches);
        Assert.NotEmpty(result.Patches);

        // Each patch should have an 'op' field
        foreach (var p in result.Patches)
        {
            Assert.True(p.ContainsKey("op"));
        }
    }

    [Fact]
    public void HasPendingChanges_AfterDetection_ReturnsTrue()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);

        // Modify the file externally
        ModifyDocx(filePath, "Modified content");

        // Act — gate detects changes
        var pending = _gate.CheckForChanges(_sessionManager.TenantId, _sessionManager, session.Id);

        // Assert
        Assert.NotNull(pending);
        Assert.True(_gate.HasPendingChanges(_sessionManager.TenantId, session.Id));
    }

    [Fact]
    public void AcknowledgeChange_MarksPatchAsAcknowledged()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);
        ModifyDocx(filePath, "Modified content");
        _gate.CheckForChanges(_sessionManager.TenantId, _sessionManager, session.Id);

        // Act
        var acknowledged = _gate.Acknowledge(_sessionManager.TenantId, session.Id);

        // Assert
        Assert.True(acknowledged);
        Assert.False(_gate.HasPendingChanges(_sessionManager.TenantId, session.Id));
    }

    [Fact]
    public void CheckForChanges_ReturnsCorrectChangeDetails()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);
        ModifyDocx(filePath, "Modified content");

        // Act
        var pending = _gate.CheckForChanges(_sessionManager.TenantId, _sessionManager, session.Id);

        // Assert
        Assert.NotNull(pending);
        Assert.Equal(session.Id, pending.SessionId);
        Assert.Equal(filePath, pending.SourcePath);
        Assert.True(pending.Summary.TotalChanges > 0);
    }

    [Fact]
    public void ClearPending_RemovesPendingState()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);
        ModifyDocx(filePath, "Modified content");
        _gate.CheckForChanges(_sessionManager.TenantId, _sessionManager, session.Id);
        Assert.True(_gate.HasPendingChanges(_sessionManager.TenantId, session.Id));

        // Act
        _gate.ClearPending(_sessionManager.TenantId, session.Id);

        // Assert
        Assert.False(_gate.HasPendingChanges(_sessionManager.TenantId, session.Id));
    }

    [Fact]
    public void NotifyExternalChange_SetsPendingState()
    {
        // Arrange
        var filePath = CreateTempDocx("Original content");
        var session = OpenSession(filePath);
        ModifyDocx(filePath, "Modified content");

        // Act — simulate gRPC notification
        _gate.NotifyExternalChange(_sessionManager.TenantId, _sessionManager, session.Id);

        // Assert
        Assert.True(_gate.HasPendingChanges(_sessionManager.TenantId, session.Id));
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
        var session = _sessionManager.Open(filePath);
        _sessions.Add(session);
        return session;
    }

    #endregion

    public void Dispose()
    {
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
