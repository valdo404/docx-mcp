using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Helpers;

namespace DocxMcp.Diff;

/// <summary>
/// Computes structured diffs between two Word documents.
/// Uses stable element IDs (dmcp:id, w14:paraId) for precise change tracking.
/// </summary>
public static class DiffEngine
{
    /// <summary>
    /// Compare two documents and produce a diff result.
    /// </summary>
    /// <param name="original">The original/baseline document.</param>
    /// <param name="modified">The modified/new document.</param>
    /// <returns>A DiffResult containing all detected changes and generated patches.</returns>
    public static DiffResult Compare(WordprocessingDocument original, WordprocessingDocument modified)
    {
        var originalBody = original.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Original document has no body.");
        var modifiedBody = modified.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Modified document has no body.");

        // Ensure both documents have IDs assigned
        ElementIdManager.EnsureNamespace(original);
        ElementIdManager.EnsureAllIds(original);
        ElementIdManager.EnsureNamespace(modified);
        ElementIdManager.EnsureAllIds(modified);

        // Take snapshots of all elements
        var originalSnapshots = CaptureSnapshots(originalBody, "/body");
        var modifiedSnapshots = CaptureSnapshots(modifiedBody, "/body");

        // Build lookup dictionaries by ID
        var originalById = originalSnapshots.ToDictionary(s => s.Id);
        var modifiedById = modifiedSnapshots.ToDictionary(s => s.Id);

        var changes = new List<ElementChange>();

        // Detect removals: elements in original but not in modified
        foreach (var snap in originalSnapshots)
        {
            if (!modifiedById.ContainsKey(snap.Id))
            {
                changes.Add(new ElementChange
                {
                    ChangeType = ChangeType.Removed,
                    ElementId = snap.Id,
                    ElementType = snap.ElementType,
                    OldPath = snap.Path,
                    OldIndex = snap.Index,
                    OldText = snap.Text,
                    OldValue = snap.JsonValue
                });
            }
        }

        // Detect additions: elements in modified but not in original
        foreach (var snap in modifiedSnapshots)
        {
            if (!originalById.ContainsKey(snap.Id))
            {
                changes.Add(new ElementChange
                {
                    ChangeType = ChangeType.Added,
                    ElementId = snap.Id,
                    ElementType = snap.ElementType,
                    NewPath = snap.Path,
                    NewIndex = snap.Index,
                    NewText = snap.Text,
                    NewValue = CreateValueForPatch(snap)
                });
            }
        }

        // Detect modifications and moves: elements in both
        foreach (var modSnap in modifiedSnapshots)
        {
            if (!originalById.TryGetValue(modSnap.Id, out var origSnap))
                continue; // Already handled as addition

            // Check if content changed
            bool contentChanged = !origSnap.ContentEquals(modSnap);

            // Check if position changed
            bool positionChanged = origSnap.Index != modSnap.Index;

            if (contentChanged)
            {
                changes.Add(new ElementChange
                {
                    ChangeType = ChangeType.Modified,
                    ElementId = modSnap.Id,
                    ElementType = modSnap.ElementType,
                    OldPath = origSnap.Path,
                    NewPath = modSnap.Path,
                    OldIndex = origSnap.Index,
                    NewIndex = modSnap.Index,
                    OldText = origSnap.Text,
                    NewText = modSnap.Text,
                    OldValue = origSnap.JsonValue,
                    NewValue = CreateValueForPatch(modSnap)
                });
            }
            else if (positionChanged)
            {
                changes.Add(new ElementChange
                {
                    ChangeType = ChangeType.Moved,
                    ElementId = modSnap.Id,
                    ElementType = modSnap.ElementType,
                    OldPath = origSnap.Path,
                    NewPath = BuildInsertPath(modSnap),
                    OldIndex = origSnap.Index,
                    NewIndex = modSnap.Index,
                    OldText = origSnap.Text,
                    NewText = modSnap.Text
                });
            }
        }

        return new DiffResult { Changes = changes };
    }

    /// <summary>
    /// Compare two documents from byte arrays.
    /// </summary>
    public static DiffResult Compare(byte[] originalBytes, byte[] modifiedBytes)
    {
        // We need to open in edit mode to allow EnsureAllIds to assign IDs
        using var originalStream = new MemoryStream();
        originalStream.Write(originalBytes);
        originalStream.Position = 0;

        using var modifiedStream = new MemoryStream();
        modifiedStream.Write(modifiedBytes);
        modifiedStream.Position = 0;

        using var originalDoc = WordprocessingDocument.Open(originalStream, isEditable: true);
        using var modifiedDoc = WordprocessingDocument.Open(modifiedStream, isEditable: true);

        return Compare(originalDoc, modifiedDoc);
    }

    /// <summary>
    /// Compare two documents from file paths.
    /// </summary>
    public static DiffResult Compare(string originalPath, string modifiedPath)
    {
        var originalBytes = File.ReadAllBytes(originalPath);
        var modifiedBytes = File.ReadAllBytes(modifiedPath);

        return Compare(originalBytes, modifiedBytes);
    }

    /// <summary>
    /// Compare a DocxSession's current state with a file on disk.
    /// Useful for detecting external modifications.
    /// </summary>
    public static DiffResult CompareSessionWithFile(DocxSession session, string filePath)
    {
        var sessionBytes = session.ToBytes();
        var fileBytes = File.ReadAllBytes(filePath);

        return Compare(sessionBytes, fileBytes);
    }

    /// <summary>
    /// Capture snapshots of all top-level body elements.
    /// </summary>
    private static List<ElementSnapshot> CaptureSnapshots(Body body, string basePath)
    {
        var snapshots = new List<ElementSnapshot>();
        int contentIndex = 0; // Index among content elements only

        foreach (var element in body.ChildElements)
        {
            // Only track content elements (paragraphs, tables)
            if (element is Paragraph or Table)
            {
                snapshots.Add(ElementSnapshot.FromElement(element, contentIndex, basePath));
                contentIndex++;
            }
        }

        return snapshots;
    }

    /// <summary>
    /// Create a JSON value suitable for a patch operation.
    /// </summary>
    private static JsonObject CreateValueForPatch(ElementSnapshot snapshot)
    {
        var value = new JsonObject
        {
            ["type"] = snapshot.ElementType
        };

        // Copy relevant properties from the snapshot's JSON
        foreach (var prop in snapshot.JsonValue)
        {
            if (prop.Key == "type" || prop.Key == "id")
                continue;

            value[prop.Key] = prop.Value is not null
                ? JsonNode.Parse(prop.Value.ToJsonString())
                : null;
        }

        return value;
    }

    /// <summary>
    /// Build a path suitable for an insert operation.
    /// </summary>
    private static string BuildInsertPath(ElementSnapshot snapshot)
    {
        return $"/body/children/{snapshot.Index}";
    }
}

/// <summary>
/// Extension methods for comparing documents.
/// </summary>
public static class DiffExtensions
{
    /// <summary>
    /// Compare this session with another session.
    /// </summary>
    public static DiffResult CompareTo(this DocxSession session, DocxSession other)
    {
        return DiffEngine.Compare(session.Document, other.Document);
    }

    /// <summary>
    /// Compare this session with a file on disk.
    /// </summary>
    public static DiffResult CompareToFile(this DocxSession session, string filePath)
    {
        return DiffEngine.CompareSessionWithFile(session, filePath);
    }

    /// <summary>
    /// Check if the source file has been modified externally.
    /// </summary>
    public static bool HasExternalChanges(this DocxSession session)
    {
        if (session.SourcePath is null)
            return false;

        if (!File.Exists(session.SourcePath))
            return false;

        var diff = session.CompareToFile(session.SourcePath);
        return diff.HasChanges;
    }
}
