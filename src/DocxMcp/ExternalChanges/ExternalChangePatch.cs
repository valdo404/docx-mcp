using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocxMcp.Diff;

namespace DocxMcp.ExternalChanges;

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
            WalPosition = walPosition,
            Message = $"Synced: +{summary.Added} -{summary.Removed} ~{summary.Modified}{uncoveredMsg}. WAL position: {walPosition}"
        };
    }
}

/// <summary>
/// JSON serialization context for external changes (AOT-safe).
/// </summary>
[JsonSerializable(typeof(ExternalElementChange))]
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
