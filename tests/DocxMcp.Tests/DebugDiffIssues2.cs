using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Diff;
using DocxMcp.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace DocxMcp.Tests;

/// <summary>
/// Deeper debugging for diff inconsistencies (issue #34).
/// </summary>
public class DebugDiffIssues2 : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<DocxSession> _sessions = [];

    public DebugDiffIssues2(ITestOutputHelper output) => _output = output;

    // ===================================================================
    // Why does round-trip change header/footer OuterXml?
    // ===================================================================

    [Fact]
    public void Debug_RoundTrip_HeaderXmlDifference()
    {
        // Create doc with header, capture OuterXml, round-trip, capture again
        var ms = new MemoryStream();
        string originalHeaderXml;
        string originalFooterXml;

        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("Body")))));

            var headerPart = mainPart.AddNewPart<HeaderPart>();
            headerPart.Header = new Header(new Paragraph(new Run(new Text("Header text"))));
            headerPart.Header.Save();

            var footerPart = mainPart.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(new Paragraph(new Run(new Text("Footer text"))));
            footerPart.Footer.Save();

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

            originalHeaderXml = headerPart.Header.OuterXml;
            originalFooterXml = footerPart.Footer.OuterXml;
        }

        _output.WriteLine("=== ORIGINAL Header XML ===");
        _output.WriteLine(originalHeaderXml);
        _output.WriteLine("\n=== ORIGINAL Footer XML ===");
        _output.WriteLine(originalFooterXml);

        // Round-trip via bytes
        var bytes = ms.ToArray();
        using var ms2 = new MemoryStream(bytes);
        using var doc2 = WordprocessingDocument.Open(ms2, false);

        var reloadedHeaderXml = doc2.MainDocumentPart!.HeaderParts.First().Header!.OuterXml;
        var reloadedFooterXml = doc2.MainDocumentPart!.FooterParts.First().Footer!.OuterXml;

        _output.WriteLine("\n=== RELOADED Header XML ===");
        _output.WriteLine(reloadedHeaderXml);
        _output.WriteLine("\n=== RELOADED Footer XML ===");
        _output.WriteLine(reloadedFooterXml);

        var headersMatch = originalHeaderXml == reloadedHeaderXml;
        var footersMatch = originalFooterXml == reloadedFooterXml;

        _output.WriteLine($"\nHeaders match: {headersMatch}");
        _output.WriteLine($"Footers match: {footersMatch}");

        if (!headersMatch)
        {
            _output.WriteLine("\n==> Header XML changed during round-trip!");
            _output.WriteLine($"Original length: {originalHeaderXml.Length}");
            _output.WriteLine($"Reloaded length: {reloadedHeaderXml.Length}");
        }
        if (!footersMatch)
        {
            _output.WriteLine("\n==> Footer XML changed during round-trip!");
            _output.WriteLine($"Original length: {originalFooterXml.Length}");
            _output.WriteLine($"Reloaded length: {reloadedFooterXml.Length}");
        }
    }

    // ===================================================================
    // Harder attempts to reproduce spurious moves
    // ===================================================================

    [Fact]
    public void Debug_SpuriousMoves_WithIdsOnOneSide()
    {
        // The real scenario: one document has dmcp:ids, the other doesn't
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Bonjour,"));
        body.AppendChild(CreateParagraph("Caca prout,"));
        body.AppendChild(CreateParagraph("Je suis titulaire."));
        body.AppendChild(CreateParagraph(""));
        body.AppendChild(CreateParagraph("Cordialement,"));
        body.AppendChild(CreateParagraph(""));
        body.AppendChild(CreateParagraph("Veuillez trouver."));
        body.AppendChild(CreateParagraph(""));

        // Add IDs to original (simulates MCP session)
        ElementIdManager.EnsureNamespace(original.Document);
        ElementIdManager.EnsureAllIds(original.Document);
        var sessionBytes = original.ToBytes();

        // Create modified version WITHOUT IDs (simulates external file)
        var fileDoc = CreateSession();
        var fileBody = fileDoc.GetBody();
        fileBody.AppendChild(CreateParagraph("Bonjour,"));
        fileBody.AppendChild(CreateParagraph("Caca prout 2,"));  // modified
        fileBody.AppendChild(CreateParagraph("Je suis titulaire."));
        fileBody.AppendChild(CreateParagraph(""));
        fileBody.AppendChild(CreateParagraph("Cordialement,"));
        fileBody.AppendChild(CreateParagraph(""));
        fileBody.AppendChild(CreateParagraph("Veuillez trouver."));
        fileBody.AppendChild(CreateParagraph(""));
        var fileBytes = fileDoc.ToBytes();

        // Diff: session (with IDs) vs file (without IDs)
        var diff1 = DiffEngine.Compare(sessionBytes, fileBytes);
        _output.WriteLine("=== Diff: session(+IDs) vs file(-IDs) ===");
        _output.WriteLine($"Total: {diff1.Summary.TotalChanges}, Modified: {diff1.Summary.Modified}, Moved: {diff1.Summary.Moved}, Added: {diff1.Summary.Added}");
        foreach (var c in diff1.Changes)
        {
            _output.WriteLine($"  [{c.ChangeType}] old:{c.OldIndex} new:{c.NewIndex} \"{Trunc(c.OldText)}\" -> \"{Trunc(c.NewText)}\"");
        }

        // Now simulate sync: load file bytes, add IDs, save
        var synced = DocxSession.FromBytes(fileBytes, "synced", null);
        _sessions.Add(synced);
        ElementIdManager.EnsureNamespace(synced.Document);
        ElementIdManager.EnsureAllIds(synced.Document);
        var syncedBytes = synced.ToBytes();

        // Diff: synced session (with new IDs) vs file (without IDs)
        var diff2 = DiffEngine.Compare(syncedBytes, fileBytes);
        _output.WriteLine("\n=== Diff: synced(+newIDs) vs file(-IDs) ===");
        _output.WriteLine($"Total: {diff2.Summary.TotalChanges}, Modified: {diff2.Summary.Modified}, Moved: {diff2.Summary.Moved}, Added: {diff2.Summary.Added}");
        foreach (var c in diff2.Changes)
        {
            _output.WriteLine($"  [{c.ChangeType}] old:{c.OldIndex} new:{c.NewIndex} \"{Trunc(c.OldText)}\" -> \"{Trunc(c.NewText)}\"");
        }
    }

    [Fact]
    public void Debug_Fingerprints_WithAndWithoutIds()
    {
        // Check if fingerprints change when dmcp:id is added
        var para1 = new Paragraph(new Run(new Text("Hello")));
        var snap1 = ElementSnapshot.FromElement(para1, 0, "/body");

        // Same paragraph with dmcp:id
        var para2 = new Paragraph(new Run(new Text("Hello")));
        ElementIdManager.SetDmcpId(para2, "12345678");
        var snap2 = ElementSnapshot.FromElement(para2, 0, "/body");

        _output.WriteLine($"Without ID - Fingerprint: {snap1.Fingerprint}, Text: \"{snap1.Text}\"");
        _output.WriteLine($"With ID    - Fingerprint: {snap2.Fingerprint}, Text: \"{snap2.Text}\"");
        _output.WriteLine($"Fingerprints match: {snap1.Fingerprint == snap2.Fingerprint}");

        // Empty paragraphs
        var empty1 = new Paragraph();
        var snapE1 = ElementSnapshot.FromElement(empty1, 0, "/body");
        var empty2 = new Paragraph();
        ElementIdManager.SetDmcpId(empty2, "ABCDEF01");
        var snapE2 = ElementSnapshot.FromElement(empty2, 0, "/body");

        _output.WriteLine($"\nEmpty without ID - Fingerprint: {snapE1.Fingerprint}, Text: \"{snapE1.Text}\"");
        _output.WriteLine($"Empty with ID    - Fingerprint: {snapE2.Fingerprint}, Text: \"{snapE2.Text}\"");
        _output.WriteLine($"Empty fingerprints match: {snapE1.Fingerprint == snapE2.Fingerprint}");
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

    private static Paragraph CreateParagraph(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new Paragraph();
        return new Paragraph(new Run(new Text(text)));
    }

    private static string Trunc(string? s) =>
        s is null ? "" : s.Length > 40 ? s[..37] + "..." : s;

    public void Dispose()
    {
        foreach (var s in _sessions)
            try { s.Dispose(); } catch { }
    }
}
