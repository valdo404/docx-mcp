using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocxMcp.Diff;

namespace DocxMcp.ExternalChanges;

/// <summary>
/// Represents an external change event detected on a document.
/// Contains the diff and generated patches for the LLM to review.
/// </summary>
public sealed class ExternalChangePatch
{
    /// <summary>
    /// Unique identifier for this external change event.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Session ID this change applies to.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// When the external change was detected.
    /// </summary>
    public required DateTime DetectedAt { get; init; }

    /// <summary>
    /// Path to the source file that was modified.
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Hash of the file before the external change (session state).
    /// </summary>
    public required string PreviousHash { get; init; }

    /// <summary>
    /// Hash of the file after the external change (new external state).
    /// </summary>
    public required string NewHash { get; init; }

    /// <summary>
    /// Summary of changes detected.
    /// </summary>
    public required DiffSummary Summary { get; init; }

    /// <summary>
    /// List of individual changes detected.
    /// </summary>
    public required List<ExternalElementChange> Changes { get; init; }

    /// <summary>
    /// Generated patches that would transform the session to match the external file.
    /// </summary>
    public required List<JsonObject> Patches { get; init; }

    /// <summary>
    /// Whether this change has been acknowledged by the LLM.
    /// </summary>
    public bool Acknowledged { get; set; }

    /// <summary>
    /// When the change was acknowledged (if applicable).
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Convert to a human-readable summary for the LLM.
    /// </summary>
    public string ToLlmSummary()
    {
        var lines = new List<string>
        {
            $"## External Document Change Detected",
            $"",
            $"**Session**: {SessionId}",
            $"**File**: {SourcePath}",
            $"**Detected at**: {DetectedAt:yyyy-MM-dd HH:mm:ss UTC}",
            $"",
            $"### Summary",
            $"- **Added**: {Summary.Added} element(s)",
            $"- **Removed**: {Summary.Removed} element(s)",
            $"- **Modified**: {Summary.Modified} element(s)",
            $"- **Moved**: {Summary.Moved} element(s)",
            $"- **Total changes**: {Summary.TotalChanges}",
            $""
        };

        if (Changes.Count > 0)
        {
            lines.Add("### Changes");
            foreach (var change in Changes.Take(20)) // Limit to first 20
            {
                lines.Add($"- {change.Description}");
            }

            if (Changes.Count > 20)
            {
                lines.Add($"- ... and {Changes.Count - 20} more changes");
            }
        }

        lines.Add("");
        lines.Add("### Required Action");
        lines.Add("You must acknowledge this external change before continuing to edit the document.");
        lines.Add("Use `acknowledge_external_change` to proceed.");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Convert to JSON for storage/transmission.
    /// </summary>
    public string ToJson(bool indented = false)
    {
        return JsonSerializer.Serialize(this, ExternalChangeJsonContext.Default.ExternalChangePatch);
    }

    /// <summary>
    /// Parse from JSON.
    /// </summary>
    public static ExternalChangePatch? FromJson(string json)
    {
        return JsonSerializer.Deserialize(json, ExternalChangeJsonContext.Default.ExternalChangePatch);
    }
}

/// <summary>
/// Simplified change record for external changes (without OpenXML references).
/// </summary>
public sealed class ExternalElementChange
{
    public required string ChangeType { get; init; }
    public required string ElementType { get; init; }
    public required string Description { get; init; }
    public int? OldIndex { get; init; }
    public int? NewIndex { get; init; }
    public string? OldText { get; init; }
    public string? NewText { get; init; }

    public static ExternalElementChange FromElementChange(ElementChange change)
    {
        return new ExternalElementChange
        {
            ChangeType = change.ChangeType.ToString().ToLowerInvariant(),
            ElementType = change.ElementType,
            Description = change.Description,
            OldIndex = change.OldIndex,
            NewIndex = change.NewIndex,
            OldText = change.OldText,
            NewText = change.NewText
        };
    }
}

/// <summary>
/// Collection of pending external changes for a session.
/// </summary>
public sealed class PendingExternalChanges
{
    /// <summary>
    /// Session ID.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// List of unacknowledged external changes (most recent first).
    /// </summary>
    public List<ExternalChangePatch> Changes { get; init; } = [];

    /// <summary>
    /// Whether there are pending changes that need acknowledgment.
    /// </summary>
    public bool HasPendingChanges => Changes.Any(c => !c.Acknowledged);

    /// <summary>
    /// Get the most recent unacknowledged change.
    /// </summary>
    public ExternalChangePatch? MostRecentPending =>
        Changes.FirstOrDefault(c => !c.Acknowledged);
}

/// <summary>
/// Result of a sync external changes operation.
/// </summary>
public sealed class SyncResult
{
    /// <summary>Whether the sync was successful.</summary>
    public required bool Success { get; init; }

    /// <summary>Human-readable message.</summary>
    public required string Message { get; init; }

    /// <summary>Whether any changes were detected.</summary>
    public bool HasChanges { get; init; }

    /// <summary>Summary of body changes (if any).</summary>
    public DiffSummary? Summary { get; init; }

    /// <summary>List of uncovered changes (headers, footers, images, etc.).</summary>
    public List<UncoveredChange>? UncoveredChanges { get; init; }

    /// <summary>The change ID that was acknowledged (if any).</summary>
    public string? AcknowledgedChangeId { get; init; }

    /// <summary>Position in WAL after sync.</summary>
    public int? WalPosition { get; init; }

    /// <summary>JSON patches representing the body changes.</summary>
    public List<JsonObject>? Patches { get; init; }

    public static SyncResult NoChanges() => new()
    {
        Success = true,
        HasChanges = false,
        Message = "No external changes detected. Document is in sync."
    };

    public static SyncResult Failure(string message) => new()
    {
        Success = false,
        HasChanges = false,
        Message = message
    };

    public static SyncResult Synced(
        DiffSummary summary,
        List<UncoveredChange> uncoveredChanges,
        List<JsonObject> patches,
        string? acknowledgedChangeId,
        int walPosition)
    {
        var uncoveredCount = uncoveredChanges.Count;
        var uncoveredMsg = uncoveredCount > 0
            ? $" ({uncoveredCount} uncovered: {string.Join(", ", uncoveredChanges.Select(u => u.Type.ToString().ToLowerInvariant()).Distinct().Take(3))})"
            : "";

        return new SyncResult
        {
            Success = true,
            HasChanges = true,
            Summary = summary,
            UncoveredChanges = uncoveredChanges,
            Patches = patches,
            AcknowledgedChangeId = acknowledgedChangeId,
            WalPosition = walPosition,
            Message = $"Synced: +{summary.Added} -{summary.Removed} ~{summary.Modified}{uncoveredMsg}. WAL position: {walPosition}"
        };
    }
}

/// <summary>
/// JSON serialization context for external changes (AOT-safe).
/// </summary>
[JsonSerializable(typeof(ExternalChangePatch))]
[JsonSerializable(typeof(ExternalElementChange))]
[JsonSerializable(typeof(PendingExternalChanges))]
[JsonSerializable(typeof(DiffSummary))]
[JsonSerializable(typeof(SyncResult))]
[JsonSerializable(typeof(UncoveredChange))]
[JsonSerializable(typeof(UncoveredChangeType))]
[JsonSerializable(typeof(List<ExternalElementChange>))]
[JsonSerializable(typeof(List<UncoveredChange>))]
[JsonSerializable(typeof(List<JsonObject>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
public partial class ExternalChangeJsonContext : JsonSerializerContext
{
}
