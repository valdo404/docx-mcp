using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.ExternalChanges;
using DocxMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocxMcp.Tests;

public class AutoSaveTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempFile;

    public AutoSaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "docx-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _tempFile = Path.Combine(_tempDir, "test.docx");
        CreateTestDocx(_tempFile, "Original content");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private SessionManager CreateManager() => TestHelpers.CreateSessionManager();
    private SyncManager CreateSyncManager() => TestHelpers.CreateSyncManager();

    [Fact]
    public void AppendWal_WithAutoSave_SavesFileOnDisk()
    {
        var mgr = CreateManager();
        var sync = CreateSyncManager();
        var session = mgr.Open(_tempFile);

        // Register source for auto-save (caller-orchestrated)
        sync.RegisterAndWatch(mgr.TenantId, session.Id, _tempFile, autoSync: true);

        // Record original file bytes
        var originalBytes = File.ReadAllBytes(_tempFile);

        // Mutate document in-memory
        var body = session.Document.MainDocumentPart!.Document!.Body!;
        body.AppendChild(new Paragraph(new Run(new Text("Added paragraph"))));

        // Append WAL then auto-save (caller-orchestrated pattern)
        var currentBytes = session.ToBytes();
        mgr.AppendWal(session.Id,
            "[{\"op\":\"add\",\"path\":\"/body/children/-1\",\"value\":{\"type\":\"paragraph\",\"text\":\"Added paragraph\"}}]", null, currentBytes);
        sync.MaybeAutoSave(mgr.TenantId, session.Id, currentBytes);

        // File on disk should have changed
        var newBytes = File.ReadAllBytes(_tempFile);
        Assert.NotEqual(originalBytes, newBytes);

        // Verify the saved file contains the new content
        using var ms = new MemoryStream(newBytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var text = string.Join(" ", doc.MainDocumentPart!.Document!.Body!
            .Descendants<Text>().Select(t => t.Text));
        Assert.Contains("Added paragraph", text);
    }

    [Fact]
    public void DryRun_DoesNotTriggerAutoSave()
    {
        var mgr = CreateManager();
        var sync = CreateSyncManager();
        var session = mgr.Open(_tempFile);

        var originalBytes = File.ReadAllBytes(_tempFile);

        // Apply patch with dry_run — this skips AppendWal entirely
        PatchTool.ApplyPatch(mgr, sync, TestHelpers.CreateExternalChangeGate(), session.Id,
            "[{\"op\":\"add\",\"path\":\"/body/children/-1\",\"value\":{\"type\":\"paragraph\",\"text\":\"Dry run\"}}]",
            dry_run: true);

        var afterBytes = File.ReadAllBytes(_tempFile);
        Assert.Equal(originalBytes, afterBytes);
    }

    [Fact]
    public void NewDocument_NoSourcePath_NoException()
    {
        var mgr = CreateManager();
        var sync = CreateSyncManager();
        var session = mgr.Create();

        // Mutate in-memory
        var body = session.Document.MainDocumentPart!.Document!.Body!;
        body.AppendChild(new Paragraph(new Run(new Text("New content"))));

        // AppendWal + MaybeAutoSave should not throw even though there's no source path
        var ex = Record.Exception(() =>
        {
            var currentBytes = session.ToBytes();
            mgr.AppendWal(session.Id,
                "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"New content\"}}]", null, currentBytes);
            sync.MaybeAutoSave(mgr.TenantId, session.Id, currentBytes);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void AutoSaveDisabled_FileUnchanged()
    {
        // Set env var to disable auto-save
        var prev = Environment.GetEnvironmentVariable("DOCX_AUTO_SAVE");
        try
        {
            Environment.SetEnvironmentVariable("DOCX_AUTO_SAVE", "false");

            var mgr = CreateManager();
            var sync = CreateSyncManager();
            var session = mgr.Open(_tempFile);

            // Register source
            sync.RegisterAndWatch(mgr.TenantId, session.Id, _tempFile, autoSync: true);

            var originalBytes = File.ReadAllBytes(_tempFile);

            // Mutate and append WAL + try auto-save
            var body = session.Document.MainDocumentPart!.Document!.Body!;
            body.AppendChild(new Paragraph(new Run(new Text("Should not save"))));
            var currentBytes = session.ToBytes();
            mgr.AppendWal(session.Id,
                "[{\"op\":\"add\",\"path\":\"/body/children/-1\",\"value\":{\"type\":\"paragraph\",\"text\":\"Should not save\"}}]", null, currentBytes);
            sync.MaybeAutoSave(mgr.TenantId, session.Id, currentBytes);

            var afterBytes = File.ReadAllBytes(_tempFile);
            Assert.Equal(originalBytes, afterBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOCX_AUTO_SAVE", prev);
        }
    }

    [Fact]
    public void StyleOperation_TriggersAutoSave()
    {
        var mgr = CreateManager();
        var sync = CreateSyncManager();
        var session = mgr.Open(_tempFile);

        // Register source for auto-save
        sync.RegisterAndWatch(mgr.TenantId, session.Id, _tempFile, autoSync: true);

        var originalBytes = File.ReadAllBytes(_tempFile);

        // Apply style (tool calls sync.MaybeAutoSave internally)
        StyleTools.StyleElement(mgr, sync, session.Id, "{\"bold\": true}", "/body/paragraph[0]");

        var afterBytes = File.ReadAllBytes(_tempFile);
        Assert.NotEqual(originalBytes, afterBytes);
    }

    [Fact]
    public void CommentAdd_TriggersAutoSave()
    {
        var mgr = CreateManager();
        var sync = CreateSyncManager();
        var session = mgr.Open(_tempFile);

        // Register source for auto-save
        sync.RegisterAndWatch(mgr.TenantId, session.Id, _tempFile, autoSync: true);

        var originalBytes = File.ReadAllBytes(_tempFile);

        // Add comment (tool calls sync.MaybeAutoSave internally)
        CommentTools.CommentAdd(mgr, sync, session.Id, "/body/paragraph[0]", "Test comment");

        var afterBytes = File.ReadAllBytes(_tempFile);
        Assert.NotEqual(originalBytes, afterBytes);
    }

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
}
