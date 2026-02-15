using System.Text.Json.Serialization;

namespace DocxMcp.Persistence;

/// <summary>
/// Session index containing metadata about all sessions.
/// </summary>
public sealed class SessionIndex
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("sessions")]
    public List<SessionIndexEntry> Sessions { get; set; } = [];

    /// <summary>
    /// Get a session entry by ID.
    /// </summary>
    public SessionIndexEntry? GetById(string id) =>
        Sessions.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// Try to get a session entry by ID.
    /// </summary>
    public bool TryGetValue(string id, out SessionIndexEntry? entry)
    {
        entry = GetById(id);
        return entry is not null;
    }

    /// <summary>
    /// Check if a session exists.
    /// </summary>
    public bool ContainsKey(string id) =>
        Sessions.Any(s => s.Id == id);

    /// <summary>
    /// Insert or update a session entry.
    /// </summary>
    public void Upsert(SessionIndexEntry entry)
    {
        var existing = Sessions.FindIndex(s => s.Id == entry.Id);
        if (existing >= 0)
            Sessions[existing] = entry;
        else
            Sessions.Add(entry);
    }

    /// <summary>
    /// Remove a session entry by ID.
    /// </summary>
    public bool Remove(string id)
    {
        var existing = Sessions.FindIndex(s => s.Id == id);
        if (existing >= 0)
        {
            Sessions.RemoveAt(existing);
            return true;
        }
        return false;
    }
}

/// <summary>
/// A single session entry in the index.
/// </summary>
public sealed class SessionIndexEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("source_path")]
    public string? SourcePath { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("last_modified_at")]
    public DateTime LastModifiedAt { get; set; }

    [JsonPropertyName("docx_file")]
    public string? DocxFile { get; set; }

    [JsonPropertyName("wal_count")]
    public int WalCount { get; set; }

    [JsonPropertyName("cursor_position")]
    public int CursorPosition { get; set; }

    [JsonPropertyName("checkpoint_positions")]
    public List<int> CheckpointPositions { get; set; } = [];

    // Convenience property for code that uses WalPosition
    [JsonIgnore]
    public ulong WalPosition
    {
        get => (ulong)WalCount;
        set => WalCount = (int)value;
    }
}

[JsonSerializable(typeof(SessionIndex))]
[JsonSerializable(typeof(SessionIndexEntry))]
[JsonSerializable(typeof(List<SessionIndexEntry>))]
[JsonSerializable(typeof(List<int>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class SessionJsonContext : JsonSerializerContext { }
