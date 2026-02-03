using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Helpers;

namespace DocxMcp;

/// <summary>
/// Holds an open WordprocessingDocument backed by a MemoryStream.
/// Provides full read/write DOM access via the Open XML SDK.
/// </summary>
public sealed class DocxSession : IDisposable
{
    public string Id { get; }
    public MemoryStream Stream { get; }
    public WordprocessingDocument Document { get; }
    public string? SourcePath { get; }

    private DocxSession(string id, WordprocessingDocument document, MemoryStream stream, string? sourcePath)
    {
        Id = id;
        Document = document;
        Stream = stream;
        SourcePath = sourcePath;
    }

    /// <summary>
    /// Open an existing .docx file into memory for editing.
    /// Always stores the absolute path regardless of input.
    /// </summary>
    public static DocxSession Open(string path)
    {
        // Always resolve to absolute path for consistent session lookup
        var absolutePath = Path.GetFullPath(path);

        if (!File.Exists(absolutePath))
            throw new FileNotFoundException($"File not found: {absolutePath}");

        var bytes = File.ReadAllBytes(absolutePath);
        var stream = new MemoryStream();
        stream.Write(bytes);
        stream.Position = 0;
        var doc = WordprocessingDocument.Open(stream, isEditable: true);
        ElementIdManager.EnsureNamespace(doc);
        ElementIdManager.EnsureAllIds(doc);
        return new DocxSession(Guid.NewGuid().ToString("N")[..12], doc, stream, absolutePath);
    }

    /// <summary>
    /// Restore a session from persisted bytes, reusing the original session ID and source path.
    /// </summary>
    public static DocxSession FromBytes(byte[] bytes, string id, string? sourcePath)
    {
        var stream = new MemoryStream();
        stream.Write(bytes);
        stream.Position = 0;
        var doc = WordprocessingDocument.Open(stream, isEditable: true);
        ElementIdManager.EnsureNamespace(doc);
        ElementIdManager.EnsureAllIds(doc);
        return new DocxSession(id, doc, stream, sourcePath);
    }

    /// <summary>
    /// Create a new empty document in memory.
    /// </summary>
    public static DocxSession Create()
    {
        var stream = new MemoryStream();
        var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);

        // Initialize minimal document structure
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        doc.Save();

        ElementIdManager.EnsureNamespace(doc);
        ElementIdManager.EnsureAllIds(doc);
        return new DocxSession(Guid.NewGuid().ToString("N")[..12], doc, stream, sourcePath: null);
    }

    /// <summary>
    /// Save document to the specified path (or original path if null).
    /// </summary>
    public void Save(string? path = null)
    {
        var targetPath = path ?? SourcePath
            ?? throw new InvalidOperationException("No path specified and document was not opened from a file.");

        Document.Save();
        Stream.Position = 0;
        File.WriteAllBytes(targetPath, Stream.ToArray());
    }

    /// <summary>
    /// Get the raw bytes of the document in its current state.
    /// </summary>
    public byte[] ToBytes()
    {
        Document.Save();
        return Stream.ToArray();
    }

    /// <summary>
    /// Get the document body. Throws if the document structure is invalid.
    /// </summary>
    public Body GetBody()
    {
        return Document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Document has no body.");
    }

    public void Dispose()
    {
        Document.Dispose();
        Stream.Dispose();
    }
}
