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

    private SessionManager CreateManager()
    {
        var mgr = TestHelpers.CreateSessionManager();
        var tracker = new ExternalChangeTracker(mgr, NullLogger<ExternalChangeTracker>.Instance);
        mgr.SetExternalChangeTracker(tracker);
        return mgr;
    }

    [Fact]
    public void AppendWal_AutoSavesFileOnDisk()
    {
        var mgr = CreateManager();
        var session = mgr.Open(_tempFile);

        // Record original file bytes
        var originalBytes = File.ReadAllBytes(_tempFile);

        // Mutate document in-memory
        var body = session.Document.MainDocumentPart!.Document!.Body!;
        body.AppendChild(new Paragraph(new Run(new Text("Added paragraph"))));

        // Append WAL triggers auto-save
        mgr.AppendWal(session.Id,
            "[{\"op\":\"add\",\"path\":\"/body/children/-1\",\"value\":{\"type\":\"paragraph\",\"text\":\"Added paragraph\"}}]");

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
        var session = mgr.Open(_tempFile);

        var originalBytes = File.ReadAllBytes(_tempFile);

        // Apply patch with dry_run — this skips AppendWal entirely
        PatchTool.ApplyPatch(mgr, null, session.Id,
            "[{\"op\":\"add\",\"path\":\"/body/children/-1\",\"value\":{\"type\":\"paragraph\",\"text\":\"Dry run\"}}]",
            dry_run: true);

        var afterBytes = File.ReadAllBytes(_tempFile);
        Assert.Equal(originalBytes, afterBytes);
    }

    [Fact]
    public void NewDocument_NoSourcePath_NoException()
    {
        var mgr = CreateManager();
        var session = mgr.Create();

        // Mutate in-memory
        var body = session.Document.MainDocumentPart!.Document!.Body!;
        body.AppendChild(new Paragraph(new Run(new Text("New content"))));

        // AppendWal should not throw even though there's no source path
        var ex = Record.Exception(() =>
            mgr.AppendWal(session.Id,
                "[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"paragraph\",\"text\":\"New content\"}}]"));

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
            var session = mgr.Open(_tempFile);
            var originalBytes = File.ReadAllBytes(_tempFile);

            // Mutate and append WAL
            var body = session.Document.MainDocumentPart!.Document!.Body!;
            body.AppendChild(new Paragraph(new Run(new Text("Should not save"))));
            mgr.AppendWal(session.Id,
                "[{\"op\":\"add\",\"path\":\"/body/children/-1\",\"value\":{\"type\":\"paragraph\",\"text\":\"Should not save\"}}]");

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
        var session = mgr.Open(_tempFile);

        var originalBytes = File.ReadAllBytes(_tempFile);

        // Apply style (this calls AppendWal internally)
        StyleTools.StyleElement(mgr, session.Id, "{\"bold\": true}", "/body/paragraph[0]");

        var afterBytes = File.ReadAllBytes(_tempFile);
        Assert.NotEqual(originalBytes, afterBytes);
    }

    [Fact]
    public void CommentAdd_TriggersAutoSave()
    {
        var mgr = CreateManager();
        var session = mgr.Open(_tempFile);

        var originalBytes = File.ReadAllBytes(_tempFile);

        // Add comment (this calls AppendWal internally)
        CommentTools.CommentAdd(mgr, session.Id, "/body/paragraph[0]", "Test comment");

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
