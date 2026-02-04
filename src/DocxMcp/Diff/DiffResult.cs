using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocxMcp.Diff;

/// <summary>
/// Represents the complete diff result between two documents.
/// </summary>
public sealed class DiffResult
{
    /// <summary>
    /// List of changes detected between the documents.
    /// </summary>
    public List<ElementChange> Changes { get; init; } = [];

    /// <summary>
    /// List of uncovered changes (headers, footers, images, etc.) that can't be represented as body patches.
    /// </summary>
    public List<UncoveredChange> UncoveredChanges { get; init; } = [];

    /// <summary>
    /// Summary statistics of the diff.
    /// </summary>
    public DiffSummary Summary => new()
    {
        TotalChanges = Changes.Count,
        Added = Changes.Count(c => c.ChangeType == ChangeType.Added),
        Removed = Changes.Count(c => c.ChangeType == ChangeType.Removed),
        Modified = Changes.Count(c => c.ChangeType == ChangeType.Modified),
        Moved = Changes.Count(c => c.ChangeType == ChangeType.Moved)
    };

    /// <summary>
    /// Whether any body changes were detected.
    /// </summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>
    /// Whether any changes (body or uncovered) were detected.
    /// </summary>
    public bool HasAnyChanges => Changes.Count > 0 || UncoveredChanges.Count > 0;

    /// <summary>
    /// Convert the diff result to a list of patches in the project's format.
    /// </summary>
    public List<JsonObject> ToPatches()
    {
        var patches = new List<JsonObject>();

        // Process removals first (in reverse order to maintain indices)
        var removals = Changes
            .Where(c => c.ChangeType == ChangeType.Removed)
            .OrderByDescending(c => c.OldIndex)
            .ToList();

        foreach (var removal in removals)
        {
            patches.Add(new JsonObject
            {
                ["op"] = "remove",
                ["path"] = removal.OldPath
            });
        }

        // Process modifications (replace operations)
        var modifications = Changes.Where(c => c.ChangeType == ChangeType.Modified);
        foreach (var mod in modifications)
        {
            patches.Add(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = mod.OldPath,
                ["value"] = JsonNode.Parse(mod.NewValue!.ToJsonString())
            });
        }

        // Process moves
        var moves = Changes.Where(c => c.ChangeType == ChangeType.Moved);
        foreach (var move in moves)
        {
            patches.Add(new JsonObject
            {
                ["op"] = "move",
                ["from"] = move.OldPath,
                ["path"] = move.NewPath
            });
        }

        // Process additions (in order)
        var additions = Changes
            .Where(c => c.ChangeType == ChangeType.Added)
            .OrderBy(c => c.NewIndex)
            .ToList();

        foreach (var add in additions)
        {
            patches.Add(new JsonObject
            {
                ["op"] = "add",
                ["path"] = add.NewPath,
                ["value"] = JsonNode.Parse(add.NewValue!.ToJsonString())
            });
        }

        return patches;
    }

    /// <summary>
    /// Convert to JSON string representation.
    /// </summary>
    public string ToJson(bool indented = true)
    {
        var summaryJson = new JsonObject
        {
            ["total_changes"] = Summary.TotalChanges,
            ["added"] = Summary.Added,
            ["removed"] = Summary.Removed,
            ["modified"] = Summary.Modified,
            ["moved"] = Summary.Moved
        };

        var result = new JsonObject
        {
            ["summary"] = summaryJson,
            ["changes"] = new JsonArray(Changes.Select(c => (JsonNode?)c.ToJson()).ToArray()),
            ["patches"] = new JsonArray(ToPatches().Select(p => (JsonNode?)p).ToArray()),
            ["uncovered_changes"] = new JsonArray(UncoveredChanges.Select(u => (JsonNode?)new JsonObject
            {
                ["type"] = u.Type.ToString().ToLowerInvariant(),
                ["description"] = u.Description,
                ["part_uri"] = u.PartUri,
                ["change_kind"] = u.ChangeKind
            }).ToArray())
        };

        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    }
}

/// <summary>
/// Summary statistics for a diff operation.
/// </summary>
public sealed class DiffSummary
{
    public int TotalChanges { get; init; }
    public int Added { get; init; }
    public int Removed { get; init; }
    public int Modified { get; init; }
    public int Moved { get; init; }
}

/// <summary>
/// Represents a single change between two document versions.
/// </summary>
public sealed class ElementChange
{
    /// <summary>
    /// Type of change (Added, Removed, Modified, Moved).
    /// </summary>
    public required ChangeType ChangeType { get; init; }

    /// <summary>
    /// Stable element ID.
    /// </summary>
    public required string ElementId { get; init; }

    /// <summary>
    /// Type of element (paragraph, table, row, etc.).
    /// </summary>
    public required string ElementType { get; init; }

    /// <summary>
    /// Path in the original document (null for additions).
    /// </summary>
    public string? OldPath { get; init; }

    /// <summary>
    /// Path in the new document (null for removals).
    /// </summary>
    public string? NewPath { get; init; }

    /// <summary>
    /// Index in the original document (null for additions).
    /// </summary>
    public int? OldIndex { get; init; }

    /// <summary>
    /// Index in the new document (null for removals).
    /// </summary>
    public int? NewIndex { get; init; }

    /// <summary>
    /// Old text content (for modifications and removals).
    /// </summary>
    public string? OldText { get; init; }

    /// <summary>
    /// New text content (for modifications and additions).
    /// </summary>
    public string? NewText { get; init; }

    /// <summary>
    /// Old JSON value (for modifications and removals).
    /// </summary>
    public JsonObject? OldValue { get; init; }

    /// <summary>
    /// New JSON value (for modifications and additions).
    /// </summary>
    public JsonObject? NewValue { get; init; }

    /// <summary>
    /// Human-readable description of the change.
    /// </summary>
    public string Description => ChangeType switch
    {
        ChangeType.Added => $"Added {ElementType} at {NewPath}: \"{Truncate(NewText ?? "", 50)}\"",
        ChangeType.Removed => $"Removed {ElementType} from {OldPath}: \"{Truncate(OldText ?? "", 50)}\"",
        ChangeType.Modified => $"Modified {ElementType} at {OldPath}: \"{Truncate(OldText ?? "", 25)}\" → \"{Truncate(NewText ?? "", 25)}\"",
        ChangeType.Moved => $"Moved {ElementType} from {OldPath} to {NewPath}",
        _ => $"Unknown change to {ElementType}"
    };

    /// <summary>
    /// Convert to JSON representation.
    /// </summary>
    public JsonObject ToJson()
    {
        var result = new JsonObject
        {
            ["change_type"] = ChangeType.ToString().ToLowerInvariant(),
            ["element_id"] = ElementId,
            ["element_type"] = ElementType,
            ["description"] = Description
        };

        if (OldPath is not null) result["old_path"] = OldPath;
        if (NewPath is not null) result["new_path"] = NewPath;
        if (OldIndex is not null) result["old_index"] = OldIndex;
        if (NewIndex is not null) result["new_index"] = NewIndex;
        if (OldText is not null) result["old_text"] = OldText;
        if (NewText is not null) result["new_text"] = NewText;

        return result;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}

/// <summary>
/// Type of change detected in a diff.
/// </summary>
public enum ChangeType
{
    /// <summary>Element was added in the new document.</summary>
    Added,

    /// <summary>Element was removed from the original document.</summary>
    Removed,

    /// <summary>Element content was modified (same ID, different content).</summary>
    Modified,

    /// <summary>Element was moved to a different position (same ID, same content, different index).</summary>
    Moved
}

/// <summary>
/// Type of change to a document part that cannot be represented as a body patch.
/// These are "uncovered" changes that the LLM should be informed about.
/// </summary>
public enum UncoveredChangeType
{
    /// <summary>Change to header part (/word/header*.xml).</summary>
    Header,

    /// <summary>Change to footer part (/word/footer*.xml).</summary>
    Footer,

    /// <summary>Change to embedded image (/word/media/*).</summary>
    Image,

    /// <summary>Change to DrawingML content (inline drawings).</summary>
    Drawing,

    /// <summary>Change to footnotes (/word/footnotes.xml).</summary>
    Footnote,

    /// <summary>Change to endnotes (/word/endnotes.xml).</summary>
    Endnote,

    /// <summary>Change to comments (/word/comments.xml).</summary>
    Comment,

    /// <summary>Change to style definitions (/word/styles.xml).</summary>
    StyleDefinition,

    /// <summary>Change to numbering definitions (/word/numbering.xml).</summary>
    Numbering,

    /// <summary>Change to theme (/word/theme/*.xml).</summary>
    Theme,

    /// <summary>Change to document settings (/word/settings.xml).</summary>
    Settings,

    /// <summary>Change to document properties (/docProps/*).</summary>
    DocumentProperty,

    /// <summary>Change to custom XML data (/customXml/*).</summary>
    CustomXml,

    /// <summary>Change to relationships (*.rels).</summary>
    Relationship,

    /// <summary>Fallback for unrecognized part changes.</summary>
    Unknown
}

/// <summary>
/// Represents a change to a document part that cannot be represented as a patch.
/// Used to inform the LLM about external modifications to headers, footers, images, etc.
/// </summary>
public sealed class UncoveredChange
{
    /// <summary>Type of the uncovered change.</summary>
    public required UncoveredChangeType Type { get; init; }

    /// <summary>Human-readable description of the change.</summary>
    public required string Description { get; init; }

    /// <summary>URI of the affected part (e.g., "/word/header1.xml").</summary>
    public string? PartUri { get; init; }

    /// <summary>Whether the part was added, removed, or modified.</summary>
    public string ChangeKind { get; init; } = "modified";
}
