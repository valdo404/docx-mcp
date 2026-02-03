using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Helpers;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests for ContentHasher - verifies that content hashing ignores ID attributes
/// and only considers actual document content.
/// </summary>
public class ContentHasherTests : IDisposable
{
    private readonly string _tempDir;

    public ContentHasherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"docx-mcp-hash-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ComputeContentHash_IdenticalContent_SameHash()
    {
        // Arrange - two docs with same content
        var doc1Bytes = CreateDocWithContent("Hello World");
        var doc2Bytes = CreateDocWithContent("Hello World");

        // Act
        var hash1 = ContentHasher.ComputeContentHash(doc1Bytes);
        var hash2 = ContentHasher.ComputeContentHash(doc2Bytes);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeContentHash_DifferentContent_DifferentHash()
    {
        // Arrange
        var doc1Bytes = CreateDocWithContent("Hello World");
        var doc2Bytes = CreateDocWithContent("Hello Universe");

        // Act
        var hash1 = ContentHasher.ComputeContentHash(doc1Bytes);
        var hash2 = ContentHasher.ComputeContentHash(doc2Bytes);

        // Assert
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeContentHash_SameContentWithDifferentDmcpIds_SameHash()
    {
        // Arrange - create docs and add different dmcp:id attributes
        var doc1Bytes = CreateDocWithDmcpId("Test Content", "AAAAAAAA");
        var doc2Bytes = CreateDocWithDmcpId("Test Content", "BBBBBBBB");

        // Act
        var hash1 = ContentHasher.ComputeContentHash(doc1Bytes);
        var hash2 = ContentHasher.ComputeContentHash(doc2Bytes);

        // Assert - hashes should be equal despite different IDs
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeContentHash_SameContentWithDifferentParaIds_SameHash()
    {
        // Arrange - create docs with different w14:paraId values
        var doc1Bytes = CreateDocWithParaId("Same text", "00000001");
        var doc2Bytes = CreateDocWithParaId("Same text", "FFFFFFFF");

        // Act
        var hash1 = ContentHasher.ComputeContentHash(doc1Bytes);
        var hash2 = ContentHasher.ComputeContentHash(doc2Bytes);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeContentHash_SameContentWithDifferentRsidAttributes_SameHash()
    {
        // Arrange - create docs with different rsid (revision) attributes
        var doc1Bytes = CreateDocWithRsid("Content", "00112233");
        var doc2Bytes = CreateDocWithRsid("Content", "AABBCCDD");

        // Act
        var hash1 = ContentHasher.ComputeContentHash(doc1Bytes);
        var hash2 = ContentHasher.ComputeContentHash(doc2Bytes);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeContentHash_DocWithAndWithoutIds_SameHash()
    {
        // Arrange - one doc plain, one with IDs
        var plainDocBytes = CreateDocWithContent("Plain content");
        var idDocBytes = CreateDocWithDmcpId("Plain content", "12345678");

        // Act
        var hash1 = ContentHasher.ComputeContentHash(plainDocBytes);
        var hash2 = ContentHasher.ComputeContentHash(idDocBytes);

        // Assert
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeContentHash_ReturnsConsistentHash()
    {
        // Arrange
        var docBytes = CreateDocWithContent("Test");

        // Act - compute multiple times
        var hash1 = ContentHasher.ComputeContentHash(docBytes);
        var hash2 = ContentHasher.ComputeContentHash(docBytes);
        var hash3 = ContentHasher.ComputeContentHash(docBytes);

        // Assert
        Assert.Equal(hash1, hash2);
        Assert.Equal(hash2, hash3);
    }

    [Fact]
    public void ComputeContentHash_Returns16CharHex()
    {
        // Arrange
        var docBytes = CreateDocWithContent("Test");

        // Act
        var hash = ContentHasher.ComputeContentHash(docBytes);

        // Assert
        Assert.Equal(16, hash.Length);
        Assert.True(hash.All(c => "0123456789abcdef".Contains(c)));
    }

    #region Helpers

    private byte[] CreateDocWithContent(string content)
    {
        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(
            new Paragraph(new Run(new Text(content)))
        ));

        doc.Save();
        ms.Position = 0;
        return ms.ToArray();
    }

    private byte[] CreateDocWithDmcpId(string content, string dmcpId)
    {
        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        var para = new Paragraph(new Run(new Text(content)));

        // Add dmcp:id attribute
        para.SetAttribute(new OpenXmlAttribute(
            ElementIdManager.DmcpPrefix,
            "id",
            ElementIdManager.DmcpNamespace,
            dmcpId));

        mainPart.Document = new Document(new Body(para));

        // Add namespace declarations
        mainPart.Document.AddNamespaceDeclaration("mc", "http://schemas.openxmlformats.org/markup-compatibility/2006");
        mainPart.Document.AddNamespaceDeclaration(ElementIdManager.DmcpPrefix, ElementIdManager.DmcpNamespace);

        doc.Save();
        ms.Position = 0;
        return ms.ToArray();
    }

    private byte[] CreateDocWithParaId(string content, string paraId)
    {
        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        var para = new Paragraph(new Run(new Text(content)))
        {
            ParagraphId = new HexBinaryValue(paraId),
            TextId = new HexBinaryValue("00000001")
        };

        mainPart.Document = new Document(new Body(para));
        doc.Save();
        ms.Position = 0;
        return ms.ToArray();
    }

    private byte[] CreateDocWithRsid(string content, string rsidR)
    {
        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        var para = new Paragraph(new Run(new Text(content)))
        {
            RsidParagraphAddition = new HexBinaryValue(rsidR)
        };

        mainPart.Document = new Document(new Body(para));
        doc.Save();
        ms.Position = 0;
        return ms.ToArray();
    }

    #endregion

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* ignore */ }
    }
}
