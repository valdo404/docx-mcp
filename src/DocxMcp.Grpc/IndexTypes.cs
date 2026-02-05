namespace DocxMcp.Grpc;

/// <summary>
/// DTO for session index entry used in atomic index operations.
/// Named with Dto suffix to avoid conflict with proto-generated SessionIndexEntry.
/// </summary>
public sealed record SessionIndexEntryDto(
    string? SourcePath,
    DateTime CreatedAt,
    DateTime ModifiedAt,
    ulong WalPosition,
    IReadOnlyList<ulong> CheckpointPositions
);

/// <summary>
/// DTO for session info returned by list operations.
/// Named with Dto suffix to avoid conflict with proto-generated SessionInfo.
/// </summary>
public sealed record SessionInfoDto(
    string SessionId,
    string? SourcePath,
    DateTime CreatedAt,
    DateTime ModifiedAt,
    long SizeBytes
);

/// <summary>
/// DTO for checkpoint info.
/// Named with Dto suffix to avoid conflict with proto-generated CheckpointInfo.
/// </summary>
public sealed record CheckpointInfoDto(
    ulong Position,
    DateTime CreatedAt,
    long SizeBytes
);

/// <summary>
/// DTO for WAL entry.
/// Named with Dto suffix to avoid conflict with proto-generated WalEntry.
/// </summary>
public sealed record WalEntryDto(
    ulong Position,
    string Operation,
    string Path,
    byte[] PatchJson,
    DateTime Timestamp
);
