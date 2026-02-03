using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocxMcp.Models;

/// <summary>
/// AOT-compatible JSON serialization context for all patch-related types.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PatchResult))]
[JsonSerializable(typeof(List<PatchOperationResult>))]
[JsonSerializable(typeof(PatchOperationResult))]
[JsonSerializable(typeof(AddOperationResult))]
[JsonSerializable(typeof(ReplaceOperationResult))]
[JsonSerializable(typeof(RemoveOperationResult))]
[JsonSerializable(typeof(MoveOperationResult))]
[JsonSerializable(typeof(CopyOperationResult))]
[JsonSerializable(typeof(ReplaceTextOperationResult))]
[JsonSerializable(typeof(RemoveColumnOperationResult))]
[JsonSerializable(typeof(UnknownOperationResult))]
[JsonSerializable(typeof(PatchOperation))]
[JsonSerializable(typeof(List<PatchOperation>))]
[JsonSerializable(typeof(AddPatchOperation))]
[JsonSerializable(typeof(ReplacePatchOperation))]
[JsonSerializable(typeof(RemovePatchOperation))]
[JsonSerializable(typeof(MovePatchOperation))]
[JsonSerializable(typeof(CopyPatchOperation))]
[JsonSerializable(typeof(ReplaceTextPatchOperation))]
[JsonSerializable(typeof(RemoveColumnPatchOperation))]
// Input DTOs for ElementTools
[JsonSerializable(typeof(AddPatchInput))]
[JsonSerializable(typeof(AddPatchInput[]))]
[JsonSerializable(typeof(ReplacePatchInput))]
[JsonSerializable(typeof(ReplacePatchInput[]))]
[JsonSerializable(typeof(ReplaceTextPatchInput))]
[JsonSerializable(typeof(ReplaceTextPatchInput[]))]
internal partial class DocxJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Input DTO for add_element tool (replaces anonymous type).
/// </summary>
public sealed class AddPatchInput
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "add";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

/// <summary>
/// Input DTO for replace_element tool (replaces anonymous type).
/// </summary>
public sealed class ReplacePatchInput
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "replace";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

/// <summary>
/// Input DTO for replace_text tool (replaces anonymous type).
/// </summary>
public sealed class ReplaceTextPatchInput
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "replace_text";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("find")]
    public string Find { get; set; } = "";

    [JsonPropertyName("replace")]
    public string Replace { get; set; } = "";

    [JsonPropertyName("max_count")]
    public int MaxCount { get; set; } = 1;
}
