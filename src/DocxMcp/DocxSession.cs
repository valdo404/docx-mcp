using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

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
    /// </summary>
    public static DocxSession Open(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"File not found: {path}");

        var bytes = File.ReadAllBytes(path);
        var stream = new MemoryStream();
        stream.Write(bytes);
        stream.Position = 0;
        var doc = WordprocessingDocument.Open(stream, isEditable: true);
        return new DocxSession(Guid.NewGuid().ToString("N")[..12], doc, stream, path);
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
