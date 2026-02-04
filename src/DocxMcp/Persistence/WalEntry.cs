using System.Text.Json.Serialization;
using DocxMcp.Diff;

namespace DocxMcp.Persistence;

/// <summary>
/// Type of WAL entry - either a regular patch or an external sync.
/// </summary>
public enum WalEntryType
{
    /// <summary>Regular patch operation applied by the LLM/user.</summary>
    Patch = 0,

    /// <summary>External sync - document was reloaded from disk.</summary>
    ExternalSync = 1,

    /// <summary>Import - initial sync when watch starts (diff + import from disk).</summary>
    Import = 2
}

/// <summary>
/// Metadata for an external sync operation.
/// Contains the full document snapshot and change detection results.
/// </summary>
public sealed class ExternalSyncMeta
{
    /// <summary>Path to the source file that was synced.</summary>
    public required string SourcePath { get; init; }

    /// <summary>SHA256 hash of the document before sync.</summary>
    public required string PreviousHash { get; init; }

    /// <summary>SHA256 hash of the document after sync.</summary>
    public required string NewHash { get; init; }

    /// <summary>Summary of changes detected in the main body.</summary>
    public required DiffSummary Summary { get; init; }

    /// <summary>Changes to parts outside the main body (headers, footers, images, etc.).</summary>
    public List<UncoveredChange> UncoveredChanges { get; init; } = [];

    /// <summary>
    /// Full document bytes at this sync point.
    /// Stored as base64 in JSON, used for checkpoint restoration.
    /// </summary>
    public required byte[] DocumentSnapshot { get; init; }
}

public sealed class WalEntry
{
    public string Patches { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string? Description { get; set; }

    /// <summary>Type of WAL entry (Patch or ExternalSync).</summary>
    public WalEntryType EntryType { get; set; } = WalEntryType.Patch;

    /// <summary>Metadata for external sync entries (null for regular patches).</summary>
    public ExternalSyncMeta? SyncMeta { get; set; }
}

[JsonSerializable(typeof(WalEntry))]
[JsonSerializable(typeof(WalEntryType))]
[JsonSerializable(typeof(ExternalSyncMeta))]
[JsonSerializable(typeof(DiffSummary))]
[JsonSerializable(typeof(UncoveredChange))]
[JsonSerializable(typeof(UncoveredChangeType))]
[JsonSerializable(typeof(List<UncoveredChange>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class WalJsonContext : JsonSerializerContext { }
