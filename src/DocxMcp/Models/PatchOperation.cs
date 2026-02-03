using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DocxMcp.Models;

/// <summary>
/// Base class for patch operations with polymorphic deserialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(AddPatchOperation), "add")]
[JsonDerivedType(typeof(ReplacePatchOperation), "replace")]
[JsonDerivedType(typeof(RemovePatchOperation), "remove")]
[JsonDerivedType(typeof(MovePatchOperation), "move")]
[JsonDerivedType(typeof(CopyPatchOperation), "copy")]
[JsonDerivedType(typeof(ReplaceTextPatchOperation), "replace_text")]
[JsonDerivedType(typeof(RemoveColumnPatchOperation), "remove_column")]
public abstract class PatchOperation
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>
    /// Validates the operation and throws if invalid.
    /// </summary>
    public abstract void Validate();
}

/// <summary>Add operation: insert element at path.</summary>
public sealed class AddPatchOperation : PatchOperation
{
    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }

    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("Add patch must have a 'path' field.");
        if (Value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Add patch must have a 'value' field.");
    }
}

/// <summary>Replace operation: replace element or property at path.</summary>
public sealed class ReplacePatchOperation : PatchOperation
{
    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }

    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("Replace patch must have a 'path' field.");
        if (Value.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Replace patch must have a 'value' field.");
    }
}

/// <summary>Remove operation: delete element at path.</summary>
public sealed class RemovePatchOperation : PatchOperation
{
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("Remove patch must have a 'path' field.");
    }
}

/// <summary>Move operation: move element from one location to another.</summary>
public sealed class MovePatchOperation : PatchOperation
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(From))
            throw new ArgumentException("Move patch must have a 'from' field.");
        if (string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("Move patch must have a 'path' field.");
    }
}

/// <summary>Copy operation: duplicate element to another location.</summary>
public sealed class CopyPatchOperation : PatchOperation
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(From))
            throw new ArgumentException("Copy patch must have a 'from' field.");
        if (string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("Copy patch must have a 'path' field.");
    }
}

/// <summary>Replace text operation: find/replace text preserving formatting.</summary>
public sealed class ReplaceTextPatchOperation : PatchOperation
{
    [JsonPropertyName("find")]
    public string Find { get; set; } = "";

    [JsonPropertyName("replace")]
    public string Replace { get; set; } = "";

    [JsonPropertyName("max_count")]
    public int MaxCount { get; set; } = 1;

    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("replace_text must have a 'path' field.");
        if (string.IsNullOrWhiteSpace(Find))
            throw new ArgumentException("replace_text must have a 'find' field.");
        if (string.IsNullOrEmpty(Replace))
            throw new ArgumentException("'replace' cannot be empty. Use 'remove' operation to delete content.");
        if (MaxCount < 0)
            throw new ArgumentException("'max_count' must be >= 0.");
    }
}

/// <summary>Remove column operation: remove a column from a table by index.</summary>
public sealed class RemoveColumnPatchOperation : PatchOperation
{
    [JsonPropertyName("column")]
    public int Column { get; set; }

    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("remove_column must have a 'path' field.");
        if (Column < 0)
            throw new ArgumentException("'column' must be >= 0.");
    }
}

/// <summary>
/// Helper for deserializing patch operations.
/// </summary>
public static class PatchOperationParser
{
    /// <summary>
    /// Parses a JSON array of patch operations.
    /// </summary>
    public static List<PatchOperation> Parse(string json)
    {
        var array = JsonSerializer.Deserialize(json, DocxJsonContext.Default.ListPatchOperation)
            ?? throw new ArgumentException("Failed to parse patches.");
        return array;
    }

    /// <summary>
    /// Parses a single patch operation from JsonElement.
    /// </summary>
    public static PatchOperation Parse(JsonElement element)
    {
        return JsonSerializer.Deserialize(element.GetRawText(), DocxJsonContext.Default.PatchOperation)
            ?? throw new ArgumentException("Failed to parse patch operation.");
    }
}
