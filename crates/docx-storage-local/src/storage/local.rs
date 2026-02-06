use std::path::{Path, PathBuf};

use async_trait::async_trait;
use docx_storage_core::{
    CheckpointInfo, SessionIndex, SessionInfo, StorageBackend, StorageError, WalEntry,
};
#[cfg(test)]
use docx_storage_core::SessionIndexEntry;
use tokio::fs;
use tracing::{debug, instrument, warn};

/// Local filesystem storage backend.
///
/// Organizes data by tenant:
/// ```
/// {base_dir}/
///   {tenant_id}/
///     sessions/
///       index.json
///       {session_id}.docx
///       {session_id}.wal
///       {session_id}.ckpt.{position}.docx
/// ```
#[derive(Debug, Clone)]
pub struct LocalStorage {
    base_dir: PathBuf,
}

/// ZIP file signature (PK\x03\x04)
const ZIP_SIGNATURE: [u8; 4] = [0x50, 0x4B, 0x03, 0x04];

/// Length of the header prefix used by .NET's memory-mapped file format.
/// The .NET code writes an 8-byte little-endian length prefix before DOCX data.
const DOTNET_HEADER_LEN: usize = 8;

impl LocalStorage {
    /// Create a new LocalStorage with the given base directory.
    pub fn new(base_dir: impl AsRef<Path>) -> Self {
        Self {
            base_dir: base_dir.as_ref().to_path_buf(),
        }
    }

    /// Strip the .NET header prefix if present.
    ///
    /// The .NET code writes session/checkpoint files with an 8-byte length prefix
    /// (little-endian u64) before the actual DOCX content. This function detects
    /// and strips that prefix if present.
    ///
    /// Detection logic:
    /// - If file starts with ZIP signature (PK\x03\x04), return as-is
    /// - If bytes 8-11 are ZIP signature, strip first 8 bytes
    fn strip_dotnet_header(data: Vec<u8>) -> Vec<u8> {
        // Empty or too small for detection
        if data.len() < DOTNET_HEADER_LEN + ZIP_SIGNATURE.len() {
            return data;
        }

        // Check if file already starts with ZIP signature (no header)
        if data[..ZIP_SIGNATURE.len()] == ZIP_SIGNATURE {
            return data;
        }

        // Check if ZIP signature is at offset 8 (has .NET header prefix)
        if data[DOTNET_HEADER_LEN..DOTNET_HEADER_LEN + ZIP_SIGNATURE.len()] == ZIP_SIGNATURE {
            debug!("Detected .NET header prefix, stripping {} bytes", DOTNET_HEADER_LEN);
            return data[DOTNET_HEADER_LEN..].to_vec();
        }

        // Unknown format, return as-is
        data
    }

    /// Get the sessions directory for a tenant.
    fn sessions_dir(&self, tenant_id: &str) -> PathBuf {
        self.base_dir.join(tenant_id).join("sessions")
    }

    /// Get the path to a session file.
    fn session_path(&self, tenant_id: &str, session_id: &str) -> PathBuf {
        self.sessions_dir(tenant_id)
            .join(format!("{}.docx", session_id))
    }

    /// Get the path to a session's WAL file.
    fn wal_path(&self, tenant_id: &str, session_id: &str) -> PathBuf {
        self.sessions_dir(tenant_id)
            .join(format!("{}.wal", session_id))
    }

    /// Get the path to a checkpoint file.
    fn checkpoint_path(&self, tenant_id: &str, session_id: &str, position: u64) -> PathBuf {
        self.sessions_dir(tenant_id)
            .join(format!("{}.ckpt.{}.docx", session_id, position))
    }

    /// Get the path to the index file.
    fn index_path(&self, tenant_id: &str) -> PathBuf {
        self.sessions_dir(tenant_id).join("index.json")
    }

    /// Ensure the sessions directory exists.
    async fn ensure_sessions_dir(&self, tenant_id: &str) -> Result<(), StorageError> {
        let dir = self.sessions_dir(tenant_id);
        fs::create_dir_all(&dir).await.map_err(|e| {
            StorageError::Io(format!("Failed to create sessions dir {}: {}", dir.display(), e))
        })?;
        Ok(())
    }
}

#[async_trait]
impl StorageBackend for LocalStorage {
    fn backend_name(&self) -> &'static str {
        "local"
    }

    // =========================================================================
    // Session Operations
    // =========================================================================

    #[instrument(skip(self), level = "debug")]
    async fn load_session(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<Vec<u8>>, StorageError> {
        let path = self.session_path(tenant_id, session_id);
        match fs::read(&path).await {
            Ok(data) => {
                let original_len = data.len();
                let data = Self::strip_dotnet_header(data);
                debug!(
                    "Loaded session {} ({} bytes, stripped {} bytes)",
                    session_id,
                    data.len(),
                    original_len - data.len()
                );
                Ok(Some(data))
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(StorageError::Io(format!(
                "Failed to read {}: {}",
                path.display(),
                e
            ))),
        }
    }

    #[instrument(skip(self, data), level = "debug", fields(data_len = data.len()))]
    async fn save_session(
        &self,
        tenant_id: &str,
        session_id: &str,
        data: &[u8],
    ) -> Result<(), StorageError> {
        self.ensure_sessions_dir(tenant_id).await?;
        let path = self.session_path(tenant_id, session_id);

        // Write atomically via temp file
        let temp_path = path.with_extension("docx.tmp");
        fs::write(&temp_path, data).await.map_err(|e| {
            StorageError::Io(format!("Failed to write {}: {}", temp_path.display(), e))
        })?;
        fs::rename(&temp_path, &path).await.map_err(|e| {
            StorageError::Io(format!("Failed to rename to {}: {}", path.display(), e))
        })?;

        debug!("Saved session {} ({} bytes)", session_id, data.len());
        Ok(())
    }

    #[instrument(skip(self), level = "debug")]
    async fn delete_session(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<bool, StorageError> {
        let session_path = self.session_path(tenant_id, session_id);
        let wal_path = self.wal_path(tenant_id, session_id);

        let existed = session_path.exists();

        // Delete session file
        if let Err(e) = fs::remove_file(&session_path).await {
            if e.kind() != std::io::ErrorKind::NotFound {
                warn!("Failed to delete session file: {}", e);
            }
        }

        // Delete WAL
        if let Err(e) = fs::remove_file(&wal_path).await {
            if e.kind() != std::io::ErrorKind::NotFound {
                warn!("Failed to delete WAL file: {}", e);
            }
        }

        // Delete all checkpoints
        let checkpoints = self.list_checkpoints(tenant_id, session_id).await?;
        for ckpt in checkpoints {
            let ckpt_path = self.checkpoint_path(tenant_id, session_id, ckpt.position);
            if let Err(e) = fs::remove_file(&ckpt_path).await {
                if e.kind() != std::io::ErrorKind::NotFound {
                    warn!("Failed to delete checkpoint: {}", e);
                }
            }
        }

        debug!("Deleted session {} (existed: {})", session_id, existed);
        Ok(existed)
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_sessions(&self, tenant_id: &str) -> Result<Vec<SessionInfo>, StorageError> {
        let dir = self.sessions_dir(tenant_id);
        if !dir.exists() {
            return Ok(vec![]);
        }

        let mut sessions = Vec::new();
        let mut entries = fs::read_dir(&dir).await.map_err(|e| {
            StorageError::Io(format!("Failed to read dir {}: {}", dir.display(), e))
        })?;

        while let Some(entry) = entries.next_entry().await.map_err(|e| {
            StorageError::Io(format!("Failed to read dir entry: {}", e))
        })? {
            let path = entry.path();
            if path.extension().is_some_and(|ext| ext == "docx")
                && !path
                    .file_stem()
                    .is_some_and(|s| s.to_string_lossy().contains(".ckpt."))
            {
                let session_id = path
                    .file_stem()
                    .map(|s| s.to_string_lossy().to_string())
                    .unwrap_or_default();

                let metadata = entry.metadata().await.map_err(|e| {
                    StorageError::Io(format!("Failed to get metadata: {}", e))
                })?;

                let created_at = metadata
                    .created()
                    .map(chrono::DateTime::from)
                    .unwrap_or_else(|_| chrono::Utc::now());
                let modified_at = metadata
                    .modified()
                    .map(chrono::DateTime::from)
                    .unwrap_or_else(|_| chrono::Utc::now());

                sessions.push(SessionInfo {
                    session_id,
                    source_path: None, // Would need to read from index
                    created_at,
                    modified_at,
                    size_bytes: metadata.len(),
                });
            }
        }

        debug!("Listed {} sessions for tenant {}", sessions.len(), tenant_id);
        Ok(sessions)
    }

    #[instrument(skip(self), level = "debug")]
    async fn session_exists(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<bool, StorageError> {
        let path = self.session_path(tenant_id, session_id);
        Ok(path.exists())
    }

    // =========================================================================
    // Index Operations
    // =========================================================================

    #[instrument(skip(self), level = "debug")]
    async fn load_index(&self, tenant_id: &str) -> Result<Option<SessionIndex>, StorageError> {
        let path = self.index_path(tenant_id);
        match fs::read_to_string(&path).await {
            Ok(json) => {
                let index: SessionIndex = serde_json::from_str(&json).map_err(|e| {
                    StorageError::Serialization(format!("Failed to parse index: {}", e))
                })?;
                debug!("Loaded index with {} sessions", index.sessions.len());
                Ok(Some(index))
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(StorageError::Io(format!(
                "Failed to read index {}: {}",
                path.display(),
                e
            ))),
        }
    }

    #[instrument(skip(self, index), level = "debug", fields(sessions = index.sessions.len()))]
    async fn save_index(
        &self,
        tenant_id: &str,
        index: &SessionIndex,
    ) -> Result<(), StorageError> {
        self.ensure_sessions_dir(tenant_id).await?;
        let path = self.index_path(tenant_id);

        let json = serde_json::to_string_pretty(index).map_err(|e| {
            StorageError::Serialization(format!("Failed to serialize index: {}", e))
        })?;

        // Write atomically
        let temp_path = path.with_extension("json.tmp");
        fs::write(&temp_path, &json).await.map_err(|e| {
            StorageError::Io(format!("Failed to write index: {}", e))
        })?;
        fs::rename(&temp_path, &path).await.map_err(|e| {
            StorageError::Io(format!("Failed to rename index: {}", e))
        })?;

        debug!("Saved index with {} sessions", index.sessions.len());
        Ok(())
    }

    // =========================================================================
    // WAL Operations
    // =========================================================================

    #[instrument(skip(self, entries), level = "debug", fields(entries_count = entries.len()))]
    async fn append_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        entries: &[WalEntry],
    ) -> Result<u64, StorageError> {
        if entries.is_empty() {
            return Ok(0);
        }

        self.ensure_sessions_dir(tenant_id).await?;
        let path = self.wal_path(tenant_id, session_id);

        // .NET MappedWal format:
        // - 8 bytes: little-endian i64 = data length (NOT including header)
        // - JSONL data: each entry is a JSON line ending with \n
        // - Remaining bytes: unused padding (memory-mapped file pre-allocated)

        // Read existing WAL or create new
        let mut wal_data = match fs::read(&path).await {
            Ok(data) if data.len() >= 8 => {
                // Parse header to get data length (NOT including header)
                let data_len = i64::from_le_bytes(data[..8].try_into().unwrap()) as usize;
                // Total used = header (8) + data_len
                let used_len = 8 + data_len;
                // Truncate to actual used data
                let mut truncated = data;
                truncated.truncate(used_len.min(truncated.len()));
                truncated
            }
            Ok(_) | Err(_) => {
                // New file - start with 8-byte header (data_len = 0)
                vec![0u8; 8]
            }
        };

        // Append new entries as JSONL (each line ends with \n)
        let mut last_position = 0u64;
        for entry in entries {
            // Write the raw .NET WalEntry JSON bytes
            wal_data.extend_from_slice(&entry.patch_json);
            // Ensure line ends with newline
            if !entry.patch_json.ends_with(b"\n") {
                wal_data.push(b'\n');
            }
            last_position = entry.position;
        }

        // Update header with data length (excluding header itself)
        let data_len = (wal_data.len() - 8) as i64;
        wal_data[..8].copy_from_slice(&data_len.to_le_bytes());

        // Write atomically
        let temp_path = path.with_extension("wal.tmp");
        fs::write(&temp_path, &wal_data).await.map_err(|e| {
            StorageError::Io(format!("Failed to write WAL: {}", e))
        })?;
        fs::rename(&temp_path, &path).await.map_err(|e| {
            StorageError::Io(format!("Failed to rename WAL: {}", e))
        })?;

        debug!(
            "Appended {} WAL entries, last position: {}, data_len: {}",
            entries.len(),
            last_position,
            data_len
        );
        Ok(last_position)
    }

    #[instrument(skip(self), level = "debug")]
    async fn read_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        from_position: u64,
        limit: Option<u64>,
    ) -> Result<(Vec<WalEntry>, bool), StorageError> {
        let path = self.wal_path(tenant_id, session_id);

        // Read raw bytes
        let raw_data = match fs::read(&path).await {
            Ok(data) => data,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
                return Ok((vec![], false));
            }
            Err(e) => {
                return Err(StorageError::Io(format!(
                    "Failed to read WAL {}: {}",
                    path.display(),
                    e
                )));
            }
        };

        // Need at least 8 bytes for header
        if raw_data.len() < 8 {
            return Ok((vec![], false));
        }

        // .NET MappedWal format:
        // - 8 bytes: little-endian i64 = data length (NOT including header)
        // - JSONL data: each entry is a JSON line ending with \n
        let data_len = i64::from_le_bytes(raw_data[..8].try_into().unwrap()) as usize;

        // Sanity check
        if data_len == 0 {
            return Ok((vec![], false));
        }
        if 8 + data_len > raw_data.len() {
            debug!(
                "WAL {} has invalid header (data_len={}, file_size={}), using file size",
                path.display(),
                data_len,
                raw_data.len()
            );
        }

        // Extract JSONL portion
        let end = (8 + data_len).min(raw_data.len());
        let jsonl_data = &raw_data[8..end];

        // Parse as UTF-8
        let content = std::str::from_utf8(jsonl_data).map_err(|e| {
            StorageError::Io(format!("WAL {} is not valid UTF-8: {}", path.display(), e))
        })?;

        // Parse JSONL - each line is a .NET WalEntry JSON
        // Position is 1-indexed (line 1 = position 1)
        let mut entries = Vec::new();
        let limit = limit.unwrap_or(u64::MAX);
        let mut position = 1u64;

        for line in content.lines() {
            let line = line.trim();
            if line.is_empty() {
                continue;
            }

            if position >= from_position {
                // Parse to extract timestamp
                let value: serde_json::Value = serde_json::from_str(line).map_err(|e| {
                    StorageError::Serialization(format!(
                        "Failed to parse WAL entry at position {}: {}",
                        position, e
                    ))
                })?;

                let timestamp = value.get("timestamp")
                    .and_then(|v| v.as_str())
                    .and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
                    .map(|dt| dt.with_timezone(&chrono::Utc))
                    .unwrap_or_else(chrono::Utc::now);

                entries.push(WalEntry {
                    position,
                    operation: String::new(),
                    path: String::new(),
                    patch_json: line.as_bytes().to_vec(),
                    timestamp,
                });

                if entries.len() as u64 >= limit {
                    return Ok((entries, true)); // might have more
                }
            }

            position += 1;
        }

        debug!(
            "Read {} WAL entries from position {} (data_len={}, total_entries={})",
            entries.len(),
            from_position,
            data_len,
            position - 1
        );
        Ok((entries, false))
    }

    #[instrument(skip(self), level = "debug")]
    async fn truncate_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        keep_count: u64,
    ) -> Result<u64, StorageError> {
        let (entries, _) = self.read_wal(tenant_id, session_id, 0, None).await?;

        // keep_count = number of entries to keep from the beginning
        // - keep_count = 0 means "delete all entries"
        // - keep_count = 1 means "keep first entry" (position 1)
        // - keep_count = N means "keep entries with position <= N"
        let (to_keep, to_remove): (Vec<_>, Vec<_>) =
            entries.into_iter().partition(|e| e.position <= keep_count);

        let removed_count = to_remove.len() as u64;

        if removed_count == 0 {
            return Ok(0);
        }

        // Rewrite WAL with only kept entries in .NET JSONL format
        // Format: 8-byte header (data length NOT including header) + JSONL data
        let path = self.wal_path(tenant_id, session_id);

        let mut wal_data = vec![0u8; 8]; // Header placeholder

        for entry in &to_keep {
            // Write raw patch_json bytes (the .NET WalEntry JSON)
            wal_data.extend_from_slice(&entry.patch_json);
            // Ensure line ends with newline
            if !entry.patch_json.ends_with(b"\n") {
                wal_data.push(b'\n');
            }
        }

        // Update header with data length (excluding header itself)
        let data_len = (wal_data.len() - 8) as i64;
        wal_data[..8].copy_from_slice(&data_len.to_le_bytes());

        // Write atomically
        let temp_path = path.with_extension("wal.tmp");
        fs::write(&temp_path, &wal_data).await.map_err(|e| {
            StorageError::Io(format!("Failed to write WAL: {}", e))
        })?;
        fs::rename(&temp_path, &path).await.map_err(|e| {
            StorageError::Io(format!("Failed to rename WAL: {}", e))
        })?;

        debug!("Truncated WAL, removed {} entries, kept {}", removed_count, to_keep.len());
        Ok(removed_count)
    }

    // =========================================================================
    // Checkpoint Operations
    // =========================================================================

    #[instrument(skip(self, data), level = "debug", fields(data_len = data.len()))]
    async fn save_checkpoint(
        &self,
        tenant_id: &str,
        session_id: &str,
        position: u64,
        data: &[u8],
    ) -> Result<(), StorageError> {
        self.ensure_sessions_dir(tenant_id).await?;
        let path = self.checkpoint_path(tenant_id, session_id, position);

        // Write atomically
        let temp_path = path.with_extension("docx.tmp");
        fs::write(&temp_path, data).await.map_err(|e| {
            StorageError::Io(format!("Failed to write checkpoint: {}", e))
        })?;
        fs::rename(&temp_path, &path).await.map_err(|e| {
            StorageError::Io(format!("Failed to rename checkpoint: {}", e))
        })?;

        debug!(
            "Saved checkpoint at position {} ({} bytes)",
            position,
            data.len()
        );
        Ok(())
    }

    #[instrument(skip(self), level = "debug")]
    async fn load_checkpoint(
        &self,
        tenant_id: &str,
        session_id: &str,
        position: u64,
    ) -> Result<Option<(Vec<u8>, u64)>, StorageError> {
        if position == 0 {
            // Load latest checkpoint
            let checkpoints = self.list_checkpoints(tenant_id, session_id).await?;
            if let Some(latest) = checkpoints.last() {
                let path = self.checkpoint_path(tenant_id, session_id, latest.position);
                let data = fs::read(&path).await.map_err(|e| {
                    StorageError::Io(format!("Failed to read checkpoint: {}", e))
                })?;
                let original_len = data.len();
                let data = Self::strip_dotnet_header(data);
                debug!(
                    "Loaded latest checkpoint at position {} ({} bytes, stripped {} bytes)",
                    latest.position,
                    data.len(),
                    original_len - data.len()
                );
                return Ok(Some((data, latest.position)));
            }
            return Ok(None);
        }

        let path = self.checkpoint_path(tenant_id, session_id, position);
        match fs::read(&path).await {
            Ok(data) => {
                let original_len = data.len();
                let data = Self::strip_dotnet_header(data);
                debug!(
                    "Loaded checkpoint at position {} ({} bytes, stripped {} bytes)",
                    position,
                    data.len(),
                    original_len - data.len()
                );
                Ok(Some((data, position)))
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(StorageError::Io(format!(
                "Failed to read checkpoint: {}",
                e
            ))),
        }
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_checkpoints(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Vec<CheckpointInfo>, StorageError> {
        let dir = self.sessions_dir(tenant_id);
        if !dir.exists() {
            return Ok(vec![]);
        }

        let prefix = format!("{}.ckpt.", session_id);
        let mut checkpoints = Vec::new();

        let mut entries = fs::read_dir(&dir).await.map_err(|e| {
            StorageError::Io(format!("Failed to read dir: {}", e))
        })?;

        while let Some(entry) = entries.next_entry().await.map_err(|e| {
            StorageError::Io(format!("Failed to read dir entry: {}", e))
        })? {
            let path = entry.path();
            let file_name = path
                .file_name()
                .map(|s| s.to_string_lossy().to_string())
                .unwrap_or_default();

            if file_name.starts_with(&prefix) && file_name.ends_with(".docx") {
                // Extract position from filename: {session_id}.ckpt.{position}.docx
                let position_str = file_name
                    .strip_prefix(&prefix)
                    .and_then(|s| s.strip_suffix(".docx"))
                    .unwrap_or("0");

                if let Ok(position) = position_str.parse::<u64>() {
                    let metadata = entry.metadata().await.map_err(|e| {
                        StorageError::Io(format!("Failed to get metadata: {}", e))
                    })?;

                    checkpoints.push(CheckpointInfo {
                        position,
                        created_at: metadata
                            .created()
                            .map(chrono::DateTime::from)
                            .unwrap_or_else(|_| chrono::Utc::now()),
                        size_bytes: metadata.len(),
                    });
                }
            }
        }

        // Sort by position
        checkpoints.sort_by_key(|c| c.position);

        debug!(
            "Listed {} checkpoints for session {}",
            checkpoints.len(),
            session_id
        );
        Ok(checkpoints)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    async fn setup() -> (LocalStorage, TempDir) {
        let temp_dir = TempDir::new().unwrap();
        let storage = LocalStorage::new(temp_dir.path());
        (storage, temp_dir)
    }

    #[tokio::test]
    async fn test_session_crud() {
        let (storage, _temp) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let data = b"PK\x03\x04fake docx content";

        // Initially doesn't exist
        assert!(!storage.session_exists(tenant, session).await.unwrap());
        assert!(storage.load_session(tenant, session).await.unwrap().is_none());

        // Save
        storage.save_session(tenant, session, data).await.unwrap();

        // Now exists
        assert!(storage.session_exists(tenant, session).await.unwrap());

        // Load
        let loaded = storage.load_session(tenant, session).await.unwrap().unwrap();
        assert_eq!(loaded, data);

        // List
        let sessions = storage.list_sessions(tenant).await.unwrap();
        assert_eq!(sessions.len(), 1);
        assert_eq!(sessions[0].session_id, session);

        // Delete
        let existed = storage.delete_session(tenant, session).await.unwrap();
        assert!(existed);
        assert!(!storage.session_exists(tenant, session).await.unwrap());
    }

    #[tokio::test]
    async fn test_wal_operations() {
        let (storage, _temp) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";

        let entries = vec![
            WalEntry {
                position: 1,
                operation: "add".to_string(),
                path: "/body/paragraph[0]".to_string(),
                patch_json: b"{}".to_vec(),
                timestamp: chrono::Utc::now(),
            },
            WalEntry {
                position: 2,
                operation: "replace".to_string(),
                path: "/body/paragraph[0]/run[0]".to_string(),
                patch_json: b"{}".to_vec(),
                timestamp: chrono::Utc::now(),
            },
        ];

        // Append
        let last_pos = storage.append_wal(tenant, session, &entries).await.unwrap();
        assert_eq!(last_pos, 2);

        // Read all
        let (read_entries, has_more) = storage.read_wal(tenant, session, 0, None).await.unwrap();
        assert_eq!(read_entries.len(), 2);
        assert!(!has_more);

        // Read from position
        let (read_entries, _) = storage.read_wal(tenant, session, 2, None).await.unwrap();
        assert_eq!(read_entries.len(), 1);
        assert_eq!(read_entries[0].position, 2);

        // Truncate - keep first 1 entry (position <= 1), remove entry at position 2
        let removed = storage.truncate_wal(tenant, session, 1).await.unwrap();
        assert_eq!(removed, 1);

        let (read_entries, _) = storage.read_wal(tenant, session, 0, None).await.unwrap();
        assert_eq!(read_entries.len(), 1);
        assert_eq!(read_entries[0].position, 1);
    }

    #[tokio::test]
    async fn test_checkpoint_operations() {
        let (storage, _temp) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let data = b"checkpoint data";

        // Save checkpoints
        storage.save_checkpoint(tenant, session, 10, data).await.unwrap();
        storage.save_checkpoint(tenant, session, 20, data).await.unwrap();

        // List
        let checkpoints = storage.list_checkpoints(tenant, session).await.unwrap();
        assert_eq!(checkpoints.len(), 2);
        assert_eq!(checkpoints[0].position, 10);
        assert_eq!(checkpoints[1].position, 20);

        // Load specific
        let (loaded, pos) = storage.load_checkpoint(tenant, session, 10).await.unwrap().unwrap();
        assert_eq!(loaded, data);
        assert_eq!(pos, 10);

        // Load latest (position = 0)
        let (_, pos) = storage.load_checkpoint(tenant, session, 0).await.unwrap().unwrap();
        assert_eq!(pos, 20);
    }

    #[tokio::test]
    async fn test_tenant_isolation() {
        let (storage, _temp) = setup().await;
        let data = b"test data";

        // Save to tenant A
        storage.save_session("tenant-a", "session-1", data).await.unwrap();

        // Tenant B shouldn't see it
        assert!(!storage.session_exists("tenant-b", "session-1").await.unwrap());
        assert!(storage.list_sessions("tenant-b").await.unwrap().is_empty());
    }

    #[tokio::test]
    async fn test_index_save_load() {
        let (storage, _temp) = setup().await;
        let tenant = "test-tenant";

        // Initially no index
        let loaded = storage.load_index(tenant).await.unwrap();
        assert!(loaded.is_none());

        // Create and save index with sessions
        let mut index = SessionIndex::default();
        index.upsert(SessionIndexEntry {
            id: "session-1".to_string(),
            source_path: Some("/path/to/doc.docx".to_string()),
            auto_sync: true,
            created_at: chrono::Utc::now(),
            last_modified_at: chrono::Utc::now(),
            docx_file: Some("session-1.docx".to_string()),
            wal_count: 5,
            cursor_position: 5,
            checkpoint_positions: vec![],
        });

        storage.save_index(tenant, &index).await.unwrap();

        // Load and verify
        let loaded = storage.load_index(tenant).await.unwrap().unwrap();
        assert_eq!(loaded.sessions.len(), 1);
        assert!(loaded.contains("session-1"));
        assert_eq!(loaded.get("session-1").unwrap().wal_count, 5);
    }

    #[tokio::test]
    async fn test_index_concurrent_updates_sequential() {
        // Test that sequential index updates work correctly
        let (storage, _temp) = setup().await;
        let tenant = "test-tenant";

        // Simulate 10 sequential session creations
        for i in 0..10 {
            // Load current index
            let mut index = storage.load_index(tenant).await.unwrap().unwrap_or_default();

            // Add a session
            let session_id = format!("session-{}", i);
            index.upsert(SessionIndexEntry {
                id: session_id,
                source_path: None,
                auto_sync: false,
                created_at: chrono::Utc::now(),
                last_modified_at: chrono::Utc::now(),
                docx_file: None,
                wal_count: 0,
                cursor_position: 0,
                checkpoint_positions: vec![],
            });

            // Save
            storage.save_index(tenant, &index).await.unwrap();
        }

        // Verify all 10 sessions are in the index
        let final_index = storage.load_index(tenant).await.unwrap().unwrap();
        assert_eq!(final_index.sessions.len(), 10);
        for i in 0..10 {
            assert!(
                final_index.contains(&format!("session-{}", i)),
                "Missing session-{}", i
            );
        }
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn test_index_concurrent_updates_with_locking() {
        use crate::lock::{FileLock, LockManager};
        use std::sync::Arc;
        use std::time::Duration;
        use tokio::sync::Barrier;

        // Test concurrent index updates WITH locking (production pattern)
        let (storage, temp) = setup().await;
        let storage = Arc::new(storage);
        let lock_manager = Arc::new(FileLock::new(temp.path()));
        let tenant = "test-tenant";

        const NUM_TASKS: usize = 10;
        let barrier = Arc::new(Barrier::new(NUM_TASKS));
        let mut handles = vec![];

        // Spawn tasks, each adding a session WITH proper locking
        for i in 0..NUM_TASKS {
            let storage = Arc::clone(&storage);
            let lock_manager = Arc::clone(&lock_manager);
            let barrier = Arc::clone(&barrier);
            let session_id = format!("session-{}", i);
            let holder_id = format!("holder-{}", i);

            let handle = tokio::spawn(async move {
                // Wait for all tasks to be ready
                barrier.wait().await;

                // Acquire lock with retries (same pattern as service.rs)
                let ttl = Duration::from_secs(30);
                let mut acquired = false;
                for attempt in 0..100 {
                    if attempt > 0 {
                        // Exponential backoff with jitter
                        let delay = Duration::from_millis(10 + (attempt * 10) as u64);
                        tokio::time::sleep(delay).await;
                    }
                    let result = lock_manager
                        .acquire(tenant, "index", &holder_id, ttl)
                        .await
                        .expect("Lock acquire should not fail");
                    if result.acquired {
                        acquired = true;
                        break;
                    }
                }

                if !acquired {
                    panic!("Task {} failed to acquire lock after 100 attempts", i);
                }

                // Load current index
                let mut index = storage
                    .load_index(tenant)
                    .await
                    .expect("Load index failed")
                    .unwrap_or_default();

                // Add a session
                index.upsert(SessionIndexEntry {
                    id: session_id.clone(),
                    source_path: None,
                    auto_sync: false,
                    created_at: chrono::Utc::now(),
                    last_modified_at: chrono::Utc::now(),
                    docx_file: None,
                    wal_count: 0,
                    cursor_position: 0,
                    checkpoint_positions: vec![],
                });

                // Save - ensure this completes before releasing lock
                storage
                    .save_index(tenant, &index)
                    .await
                    .expect("Save index failed");

                // Release lock
                lock_manager
                    .release(tenant, "index", &holder_id)
                    .await
                    .expect("Release lock failed");

                session_id
            });

            handles.push(handle);
        }

        // Collect all session IDs
        let mut created_ids = vec![];
        for handle in handles {
            let id = handle.await.expect("Task panicked");
            created_ids.push(id);
        }

        // With proper locking, ALL sessions should be present
        let final_index = storage.load_index(tenant).await.unwrap().unwrap();
        let found_count = final_index.sessions.len();

        assert_eq!(
            found_count, NUM_TASKS,
            "All {} sessions should be in index with proper locking. Found: {}. Missing: {:?}",
            NUM_TASKS,
            found_count,
            created_ids
                .iter()
                .filter(|id| !final_index.contains(id))
                .collect::<Vec<_>>()
        );
    }

    #[tokio::test]
    async fn test_load_dotnet_index_format() {
        // Test loading the actual .NET index format
        let index_json = r#"{
  "version": 1,
  "sessions": [
    {
      "id": "a5fea612f066",
      "source_path": "/Users/laurentvaldes/Documents/lettre de motivation.docx",
      "created_at": "2026-02-03T21:16:37.29544Z",
      "last_modified_at": "2026-02-04T17:37:38.4257Z",
      "docx_file": "a5fea612f066.docx",
      "wal_count": 26,
      "cursor_position": 26,
      "checkpoint_positions": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
    }
  ]
}"#;

        let index: SessionIndex = serde_json::from_str(index_json).expect("Failed to parse index");

        assert_eq!(index.version, 1);
        assert_eq!(index.sessions.len(), 1);

        let session = index.get("a5fea612f066").expect("Session not found");
        assert_eq!(session.id, "a5fea612f066");
        assert_eq!(session.source_path, Some("/Users/laurentvaldes/Documents/lettre de motivation.docx".to_string()));
        assert_eq!(session.docx_file, Some("a5fea612f066.docx".to_string()));
        assert_eq!(session.wal_count, 26);
        assert_eq!(session.cursor_position, 26);
        assert_eq!(session.checkpoint_positions.len(), 10);
    }

    #[test]
    fn test_strip_dotnet_header_with_prefix() {
        // Simulate .NET format: 8-byte length prefix + DOCX data
        // The first 8 bytes are a little-endian u64 length
        let mut data = vec![0x87, 0x95, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]; // 8-byte header
        data.extend_from_slice(&[0x50, 0x4B, 0x03, 0x04]); // PK signature
        data.extend_from_slice(b"rest of docx content");

        let result = LocalStorage::strip_dotnet_header(data);

        // Should strip the 8-byte header
        assert_eq!(result[0..4], [0x50, 0x4B, 0x03, 0x04]);
        assert_eq!(result.len(), 4 + 20); // PK + "rest of docx content"
    }

    #[test]
    fn test_strip_dotnet_header_without_prefix() {
        // Raw DOCX file (no header) - starts directly with PK
        let mut data = vec![0x50, 0x4B, 0x03, 0x04]; // PK signature
        data.extend_from_slice(b"rest of docx content");

        let result = LocalStorage::strip_dotnet_header(data.clone());

        // Should return unchanged
        assert_eq!(result, data);
    }

    #[test]
    fn test_strip_dotnet_header_empty() {
        let data = vec![];
        let result = LocalStorage::strip_dotnet_header(data);
        assert!(result.is_empty());
    }

    #[test]
    fn test_strip_dotnet_header_too_small() {
        // Too small to have header + valid DOCX
        let data = vec![0x01, 0x02, 0x03];
        let result = LocalStorage::strip_dotnet_header(data.clone());
        assert_eq!(result, data);
    }

    #[test]
    fn test_strip_dotnet_header_unknown_format() {
        // Unknown format - doesn't start with PK and no PK at offset 8
        let data = vec![0x00; 20];
        let result = LocalStorage::strip_dotnet_header(data.clone());
        assert_eq!(result, data);
    }

    #[tokio::test]
    async fn test_load_session_with_dotnet_header() {
        let (storage, _temp) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";

        // Write a file with .NET header prefix
        storage.ensure_sessions_dir(tenant).await.unwrap();
        let path = storage.session_path(tenant, session);

        let mut data_with_header = vec![0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]; // 8-byte header
        data_with_header.extend_from_slice(&[0x50, 0x4B, 0x03, 0x04]); // PK signature
        data_with_header.extend_from_slice(b"docx content");

        fs::write(&path, &data_with_header).await.unwrap();

        // Load should strip the header
        let loaded = storage.load_session(tenant, session).await.unwrap().unwrap();
        assert_eq!(&loaded[0..4], &[0x50, 0x4B, 0x03, 0x04]);
        assert_eq!(loaded.len(), 4 + 12); // PK + "docx content"
    }

    #[tokio::test]
    async fn test_load_checkpoint_with_dotnet_header() {
        let (storage, _temp) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";

        // Write a checkpoint with .NET header prefix
        storage.ensure_sessions_dir(tenant).await.unwrap();
        let path = storage.checkpoint_path(tenant, session, 10);

        let mut data_with_header = vec![0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]; // 8-byte header
        data_with_header.extend_from_slice(&[0x50, 0x4B, 0x03, 0x04]); // PK signature
        data_with_header.extend_from_slice(b"checkpoint data");

        fs::write(&path, &data_with_header).await.unwrap();

        // Load should strip the header
        let (loaded, pos) = storage.load_checkpoint(tenant, session, 10).await.unwrap().unwrap();
        assert_eq!(pos, 10);
        assert_eq!(&loaded[0..4], &[0x50, 0x4B, 0x03, 0x04]);
        assert_eq!(loaded.len(), 4 + 15); // PK + "checkpoint data"
    }
}
