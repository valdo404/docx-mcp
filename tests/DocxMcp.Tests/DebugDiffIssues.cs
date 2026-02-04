using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Diff;
using DocxMcp.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace DocxMcp.Tests;

/// <summary>
/// Debug tests to verify hypotheses about diff inconsistencies (issue #34).
/// </summary>
public class DebugDiffIssues : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<DocxSession> _sessions = [];

    public DebugDiffIssues(ITestOutputHelper output) => _output = output;

    // ===================================================================
    // Hypothesis 1: Uncovered changes are false positives from ID attributes
    // ===================================================================

    [Fact]
    public void Debug_UncoveredChanges_FalsePositives_FromIdAttributes()
    {
        // Create a document with headers and footers
        var original = CreateDocWithHeaderFooter("Body text", "Header text", "Footer text");
        _sessions.Add(original);

        // Serialize and reload (simulates sync-external loading from file)
        var bytes = original.ToBytes();
        var reloaded = DocxSession.FromBytes(bytes, "test-reload", null);
        _sessions.Add(reloaded);

        // Now add IDs (simulates what sync-external does with EnsureAllIds)
        ElementIdManager.EnsureNamespace(reloaded.Document);
        ElementIdManager.EnsureAllIds(reloaded.Document);

        // Compare original (no IDs) vs reloaded (with IDs)
        var uncovered = DiffEngine.DetectUncoveredChanges(original.Document, reloaded.Document);

        _output.WriteLine($"Uncovered changes count: {uncovered.Count}");
        foreach (var uc in uncovered)
        {
            _output.WriteLine($"  [{uc.ChangeKind}] {uc.Type}: {uc.Description} ({uc.PartUri})");
        }

        // HYPOTHESIS: If uncovered changes are from ID attributes, we'll see false positives
        // for headers/footers even though content is identical
        if (uncovered.Count > 0)
        {
            _output.WriteLine("\n==> CONFIRMED: Uncovered changes are false positives from ID attributes!");
            _output.WriteLine("    DetectUncoveredChanges compares raw OuterXml including dmcp:id.");
        }
        else
        {
            _output.WriteLine("\n==> REFUTED: No false positives detected.");
        }
    }

    [Fact]
    public void Debug_UncoveredChanges_WithIdenticalDocuments()
    {
        // Compare a document with itself — should show zero uncovered changes
        var doc = CreateDocWithHeaderFooter("Body", "Header", "Footer");
        _sessions.Add(doc);

        var uncovered = DiffEngine.DetectUncoveredChanges(doc.Document, doc.Document);
        _output.WriteLine($"Self-comparison uncovered changes: {uncovered.Count}");
        Assert.Empty(uncovered);
    }

    [Fact]
    public void Debug_UncoveredChanges_ByteRoundtrip_NoIds()
    {
        // Round-trip without adding IDs — should show zero uncovered changes
        var original = CreateDocWithHeaderFooter("Body", "Header", "Footer");
        _sessions.Add(original);

        var reloaded = DocxSession.FromBytes(original.ToBytes(), "test-noid", null);
        _sessions.Add(reloaded);

        var uncovered = DiffEngine.DetectUncoveredChanges(original.Document, reloaded.Document);
        _output.WriteLine($"Round-trip (no IDs) uncovered changes: {uncovered.Count}");
        foreach (var uc in uncovered)
        {
            _output.WriteLine($"  [{uc.ChangeKind}] {uc.Type}: {uc.Description}");
        }
    }

    // ===================================================================
    // Hypothesis 2: Spurious moves from duplicate fingerprints
    // ===================================================================

    [Fact]
    public void Debug_SpuriousMoves_AfterSyncSimulation()
    {
        // Simulate the bug report scenario:
        // 1. Create doc with several paragraphs
        // 2. Modify one paragraph (simulating external edit)
        // 3. Diff should show 1 modification
        // 4. "Sync" by replacing session with modified bytes + adding IDs
        // 5. Diff should show 0 changes

        // Step 1: Create original document
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Bonjour,"));
        body.AppendChild(CreateParagraph("Caca prout,"));
        body.AppendChild(CreateParagraph("Je suis titulaire d'une carte de séjour."));
        body.AppendChild(CreateParagraph("")); // empty paragraph
        body.AppendChild(CreateParagraph("Cordialement,"));
        body.AppendChild(CreateParagraph("")); // another empty paragraph
        body.AppendChild(CreateParagraph("Veuillez trouver ci-joint les pièces."));
        body.AppendChild(CreateParagraph("")); // yet another empty paragraph

        // Save original to file
        var originalBytes = original.ToBytes();

        // Step 2: Create modified version (edit paragraph 2)
        var modified = DocxSession.FromBytes(originalBytes, "mod", null);
        _sessions.Add(modified);
        var modBody = modified.GetBody();
        var para2 = modBody.Elements<Paragraph>().ElementAt(1);
        // Replace text
        para2.RemoveAllChildren<Run>();
        para2.AppendChild(new Run(new Text("Caca prout 2,")));

        var modifiedBytes = modified.ToBytes();

        // Step 3: First diff — should show 1 modification
        var diff1 = DiffEngine.Compare(originalBytes, modifiedBytes);
        _output.WriteLine("=== First diff (original vs modified) ===");
        _output.WriteLine($"Total: {diff1.Summary.TotalChanges}, Modified: {diff1.Summary.Modified}, Moved: {diff1.Summary.Moved}, Added: {diff1.Summary.Added}, Removed: {diff1.Summary.Removed}");
        foreach (var c in diff1.Changes)
        {
            _output.WriteLine($"  [{c.ChangeType}] {c.ElementType} old:{c.OldIndex} new:{c.NewIndex} \"{c.OldText?[..Math.Min(40, c.OldText?.Length ?? 0)]}\" -> \"{c.NewText?[..Math.Min(40, c.NewText?.Length ?? 0)]}\"");
        }

        // Step 4: Simulate sync-external — load modified bytes, add IDs
        var synced = DocxSession.FromBytes(modifiedBytes, "synced", null);
        _sessions.Add(synced);
        ElementIdManager.EnsureNamespace(synced.Document);
        ElementIdManager.EnsureAllIds(synced.Document);
        var syncedBytes = synced.ToBytes();

        // Step 5: Diff synced session vs modified file — should show 0 changes
        var diff2 = DiffEngine.Compare(syncedBytes, modifiedBytes);
        _output.WriteLine("\n=== Second diff (synced session vs modified file) ===");
        _output.WriteLine($"Total: {diff2.Summary.TotalChanges}, Modified: {diff2.Summary.Modified}, Moved: {diff2.Summary.Moved}, Added: {diff2.Summary.Added}, Removed: {diff2.Summary.Removed}");
        foreach (var c in diff2.Changes)
        {
            _output.WriteLine($"  [{c.ChangeType}] {c.ElementType} old:{c.OldIndex} new:{c.NewIndex} \"{c.OldText?[..Math.Min(40, c.OldText?.Length ?? 0)]}\" -> \"{c.NewText?[..Math.Min(40, c.NewText?.Length ?? 0)]}\"");
        }

        if (diff2.HasChanges)
        {
            _output.WriteLine("\n==> BUG CONFIRMED: Spurious changes after sync!");
        }
        else
        {
            _output.WriteLine("\n==> No spurious changes (bug not reproduced in this scenario).");
        }
    }

    [Fact]
    public void Debug_ExactMatch_DuplicateFingerprints()
    {
        // Test exact matching with duplicate empty paragraphs
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("A"));
        body.AppendChild(CreateParagraph("")); // empty para 1
        body.AppendChild(CreateParagraph("B"));
        body.AppendChild(CreateParagraph("")); // empty para 2
        body.AppendChild(CreateParagraph("C"));

        // Modified: same content, same order
        var modified = CreateSessionFromBytes(original.ToBytes());

        var diff = DiffEngine.Compare(original.Document, modified.Document);
        _output.WriteLine($"Identical doc with duplicate fingerprints: {diff.Summary.TotalChanges} changes, {diff.Summary.Moved} moves");

        if (diff.Summary.Moved > 0)
        {
            _output.WriteLine("==> BUG: Spurious moves for duplicate empty paragraphs!");
            foreach (var c in diff.Changes.Where(c => c.ChangeType == ChangeType.Moved))
            {
                _output.WriteLine($"  Moved: old:{c.OldIndex} new:{c.NewIndex}");
            }
        }
    }

    [Fact]
    public void Debug_ExactMatch_DuplicateFingerprints_WithInsertion()
    {
        // Test with an insertion among duplicates
        var original = CreateSession();
        var origBody = original.GetBody();
        origBody.AppendChild(CreateParagraph("A"));
        origBody.AppendChild(CreateParagraph("")); // empty para
        origBody.AppendChild(CreateParagraph("B"));
        origBody.AppendChild(CreateParagraph("")); // empty para

        var modified = CreateSession();
        var modBody = modified.GetBody();
        modBody.AppendChild(CreateParagraph("A"));
        modBody.AppendChild(CreateParagraph("")); // empty para
        modBody.AppendChild(CreateParagraph("NEW PARAGRAPH")); // inserted
        modBody.AppendChild(CreateParagraph("B"));
        modBody.AppendChild(CreateParagraph("")); // empty para

        var diff = DiffEngine.Compare(original.Document, modified.Document);
        _output.WriteLine($"With insertion among duplicates: {diff.Summary.TotalChanges} changes");
        _output.WriteLine($"  Added: {diff.Summary.Added}, Removed: {diff.Summary.Removed}, Modified: {diff.Summary.Modified}, Moved: {diff.Summary.Moved}");

        foreach (var c in diff.Changes)
        {
            _output.WriteLine($"  [{c.ChangeType}] {c.ElementType} old:{c.OldIndex} new:{c.NewIndex} \"{c.OldText}\" -> \"{c.NewText}\"");
        }

        if (diff.Summary.Moved > 0)
        {
            _output.WriteLine("==> BUG: Spurious moves when inserting among duplicate elements!");
        }
        else if (diff.Summary.Added == 1 && diff.Summary.TotalChanges == 1)
        {
            _output.WriteLine("==> CORRECT: Only 1 addition detected.");
        }
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private DocxSession CreateSession()
    {
        var session = DocxSession.Create();
        _sessions.Add(session);
        return session;
    }

    private DocxSession CreateSessionFromBytes(byte[] bytes)
    {
        var session = DocxSession.FromBytes(bytes, Guid.NewGuid().ToString("N")[..12], null);
        _sessions.Add(session);
        return session;
    }

    private static DocxSession CreateDocWithHeaderFooter(string bodyText, string headerText, string footerText)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text(bodyText)))));

            // Add header
            var headerPart = mainPart.AddNewPart<HeaderPart>();
            headerPart.Header = new Header(new Paragraph(new Run(new Text(headerText))));
            headerPart.Header.Save();

            // Add footer
            var footerPart = mainPart.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(new Paragraph(new Run(new Text(footerText))));
            footerPart.Footer.Save();

            // Reference header/footer in section properties
            var sectionProps = new SectionProperties();
            sectionProps.AppendChild(new HeaderReference
            {
                Type = HeaderFooterValues.Default,
                Id = mainPart.GetIdOfPart(headerPart)
            });
            sectionProps.AppendChild(new FooterReference
            {
                Type = HeaderFooterValues.Default,
                Id = mainPart.GetIdOfPart(footerPart)
            });
            mainPart.Document.Body!.AppendChild(sectionProps);
            mainPart.Document.Save();
        }

        ms.Position = 0;
        return DocxSession.FromBytes(ms.ToArray(), Guid.NewGuid().ToString("N")[..12], null);
    }

    private static Paragraph CreateParagraph(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new Paragraph();
        return new Paragraph(new Run(new Text(text)));
    }

    public void Dispose()
    {
        foreach (var s in _sessions)
        {
            try { s.Dispose(); } catch { }
        }
    }
}
