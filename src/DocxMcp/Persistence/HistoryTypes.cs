using DocxMcp.Diff;

namespace DocxMcp.Persistence;

public sealed class UndoRedoResult
{
    public int Position { get; set; }
    public int Steps { get; set; }
    public string Message { get; set; } = "";
}

public sealed class HistoryEntry
{
    public int Position { get; set; }
    public DateTime Timestamp { get; set; }
    public string Description { get; set; } = "";
    public bool IsCurrent { get; set; }
    public bool IsCheckpoint { get; set; }

    /// <summary>Whether this entry is an external sync (document reloaded from disk).</summary>
    public bool IsExternalSync { get; set; }

    /// <summary>Summary of external sync changes (only set for external sync entries).</summary>
    public ExternalSyncSummary? SyncSummary { get; set; }
}

/// <summary>
/// Summary information for an external sync entry in history.
/// </summary>
public sealed class ExternalSyncSummary
{
    /// <summary>Path to the source file that was synced.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Number of body elements added.</summary>
    public int Added { get; init; }

    /// <summary>Number of body elements removed.</summary>
    public int Removed { get; init; }

    /// <summary>Number of body elements modified.</summary>
    public int Modified { get; init; }

    /// <summary>Number of uncovered changes (headers, footers, images, etc.).</summary>
    public int UncoveredCount { get; init; }

    /// <summary>Types of uncovered changes (e.g., "header", "image").</summary>
    public List<string> UncoveredTypes { get; init; } = [];
}

public sealed class HistoryResult
{
    public int TotalEntries { get; set; }
    public int CursorPosition { get; set; }
    public bool CanUndo { get; set; }
    public bool CanRedo { get; set; }
    public List<HistoryEntry> Entries { get; set; } = new();
}
