using System.Text.Json.Serialization;

namespace DocxMcp.Persistence;

/// <summary>
/// Session index format matching the Rust gRPC storage server.
/// Uses a HashMap/Dictionary keyed by session ID.
/// </summary>
public sealed class SessionIndex
{
    [JsonPropertyName("sessions")]
    public Dictionary<string, SessionIndexEntry> Sessions { get; set; } = new();
}

/// <summary>
/// Entry in the session index, keyed by session ID.
/// Property names use snake_case to match Rust serialization.
/// </summary>
public sealed class SessionIndexEntry
{
    [JsonPropertyName("source_path")]
    public string? SourcePath { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("modified_at")]
    public DateTime ModifiedAt { get; set; }

    [JsonPropertyName("wal_position")]
    public ulong WalPosition { get; set; }

    [JsonPropertyName("checkpoint_positions")]
    public List<ulong> CheckpointPositions { get; set; } = [];
}

// Legacy types for backwards compatibility during migration
// TODO: Remove after migration is complete

[Obsolete("Use SessionIndex instead")]
public sealed class SessionIndexFile
{
    public int Version { get; set; } = 1;
    public List<SessionEntry> Sessions { get; set; } = new();

    /// <summary>
    /// Convert legacy format to new format.
    /// </summary>
    public SessionIndex ToSessionIndex()
    {
        var index = new SessionIndex();
        foreach (var entry in Sessions)
        {
            index.Sessions[entry.Id] = new SessionIndexEntry
            {
                SourcePath = entry.SourcePath,
                CreatedAt = entry.CreatedAt,
                ModifiedAt = entry.LastModifiedAt,
                WalPosition = (ulong)entry.WalCount,
                CheckpointPositions = entry.CheckpointPositions.Select(p => (ulong)p).ToList()
            };
        }
        return index;
    }
}

[Obsolete("Use SessionIndexEntry instead")]
public sealed class SessionEntry
{
    public string Id { get; set; } = "";
    public string? SourcePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastModifiedAt { get; set; }
    public string DocxFile { get; set; } = "";
    public int WalCount { get; set; }
    public int CursorPosition { get; set; } = -1;
    public List<int> CheckpointPositions { get; set; } = new();
}

[JsonSerializable(typeof(SessionIndex))]
[JsonSerializable(typeof(SessionIndexEntry))]
[JsonSerializable(typeof(Dictionary<string, SessionIndexEntry>))]
[JsonSerializable(typeof(List<ulong>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SessionJsonContext : JsonSerializerContext { }
