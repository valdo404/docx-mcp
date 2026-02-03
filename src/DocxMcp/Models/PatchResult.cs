using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxMcp.Models;

/// <summary>
/// Standardized result for patch operations.
/// </summary>
public sealed class PatchResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("dry_run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DryRun { get; set; }

    [JsonPropertyName("applied")]
    public int Applied { get; set; }

    [JsonPropertyName("would_apply")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WouldApply { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("operations")]
    public List<PatchOperationResult> Operations { get; set; } = [];

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Base class for patch operation results with polymorphic serialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(AddOperationResult), "add")]
[JsonDerivedType(typeof(ReplaceOperationResult), "replace")]
[JsonDerivedType(typeof(RemoveOperationResult), "remove")]
[JsonDerivedType(typeof(MoveOperationResult), "move")]
[JsonDerivedType(typeof(CopyOperationResult), "copy")]
[JsonDerivedType(typeof(ReplaceTextOperationResult), "replace_text")]
[JsonDerivedType(typeof(RemoveColumnOperationResult), "remove_column")]
[JsonDerivedType(typeof(UnknownOperationResult), "unknown")]
public abstract class PatchOperationResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = ""; // "success", "error", "would_succeed", "would_fail"

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

/// <summary>Result for add operation.</summary>
public sealed class AddOperationResult : PatchOperationResult
{
    [JsonPropertyName("created_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedId { get; set; }
}

/// <summary>Result for replace operation.</summary>
public sealed class ReplaceOperationResult : PatchOperationResult
{
    [JsonPropertyName("replaced_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplacedId { get; set; }
}

/// <summary>Result for remove operation.</summary>
public sealed class RemoveOperationResult : PatchOperationResult
{
    [JsonPropertyName("removed_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemovedId { get; set; }
}

/// <summary>Result for move operation.</summary>
public sealed class MoveOperationResult : PatchOperationResult
{
    [JsonPropertyName("moved_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MovedId { get; set; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; set; }
}

/// <summary>Result for copy operation.</summary>
public sealed class CopyOperationResult : PatchOperationResult
{
    [JsonPropertyName("source_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceId { get; set; }

    [JsonPropertyName("copy_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CopyId { get; set; }
}

/// <summary>Result for replace_text operation.</summary>
public sealed class ReplaceTextOperationResult : PatchOperationResult
{
    [JsonPropertyName("matches_found")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MatchesFound { get; set; }

    [JsonPropertyName("replacements_made")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReplacementsMade { get; set; }

    [JsonPropertyName("would_replace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WouldReplace { get; set; }
}

/// <summary>Result for remove_column operation.</summary>
public sealed class RemoveColumnOperationResult : PatchOperationResult
{
    [JsonPropertyName("column_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ColumnIndex { get; set; }

    [JsonPropertyName("rows_affected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RowsAffected { get; set; }
}

/// <summary>Result for unknown/unsupported operations.</summary>
public sealed class UnknownOperationResult : PatchOperationResult
{
    [JsonPropertyName("unknown_op")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnknownOp { get; set; }
}
