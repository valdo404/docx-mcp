use async_trait::async_trait;
use serde::{Deserialize, Serialize};

use crate::error::StorageError;

/// Information about a session stored in the backend.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionInfo {
    pub session_id: String,
    pub source_path: Option<String>,
    pub created_at: chrono::DateTime<chrono::Utc>,
    pub modified_at: chrono::DateTime<chrono::Utc>,
    pub size_bytes: u64,
}

/// A single WAL entry representing an edit operation.
///
/// The `patch_json` field contains the raw JSON bytes of the .NET WalEntry.
/// The Rust server doesn't parse this - it just stores and retrieves raw bytes.
/// The `position` field is assigned by the server when appending.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WalEntry {
    /// Position in WAL (1-indexed, assigned by server)
    pub position: u64,
    /// Operation type (for debugging/logging only)
    #[serde(default)]
    pub operation: String,
    /// Target path (for debugging/logging only)
    #[serde(default)]
    pub path: String,
    /// Raw JSON bytes of the .NET WalEntry - stored as-is on disk
    #[serde(with = "serde_bytes")]
    pub patch_json: Vec<u8>,
    /// Timestamp
    pub timestamp: chrono::DateTime<chrono::Utc>,
}

/// Information about a checkpoint.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CheckpointInfo {
    pub position: u64,
    pub created_at: chrono::DateTime<chrono::Utc>,
    pub size_bytes: u64,
}

/// The session index containing metadata about all sessions for a tenant.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct SessionIndex {
    /// Schema version
    #[serde(default = "default_version")]
    pub version: u32,
    /// Array of session entries
    #[serde(default)]
    pub sessions: Vec<SessionIndexEntry>,
}

fn default_version() -> u32 {
    1
}

impl SessionIndex {
    /// Get a session entry by ID.
    #[allow(dead_code)]
    pub fn get(&self, session_id: &str) -> Option<&SessionIndexEntry> {
        self.sessions.iter().find(|s| s.id == session_id)
    }

    /// Get a mutable session entry by ID.
    pub fn get_mut(&mut self, session_id: &str) -> Option<&mut SessionIndexEntry> {
        self.sessions.iter_mut().find(|s| s.id == session_id)
    }

    /// Insert or update a session entry.
    pub fn upsert(&mut self, entry: SessionIndexEntry) {
        if let Some(existing) = self.get_mut(&entry.id) {
            *existing = entry;
        } else {
            self.sessions.push(entry);
        }
    }

    /// Remove a session entry by ID.
    pub fn remove(&mut self, session_id: &str) -> Option<SessionIndexEntry> {
        if let Some(pos) = self.sessions.iter().position(|s| s.id == session_id) {
            Some(self.sessions.remove(pos))
        } else {
            None
        }
    }

    /// Check if a session exists.
    pub fn contains(&self, session_id: &str) -> bool {
        self.sessions.iter().any(|s| s.id == session_id)
    }
}

/// A single session entry in the index.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionIndexEntry {
    /// Session ID
    pub id: String,
    /// Original source file path
    pub source_path: Option<String>,
    /// Auto-sync enabled for this session
    #[serde(default = "default_auto_sync")]
    pub auto_sync: bool,
    /// When the session was created
    pub created_at: chrono::DateTime<chrono::Utc>,
    /// When the session was last modified
    #[serde(alias = "modified_at")]
    pub last_modified_at: chrono::DateTime<chrono::Utc>,
    /// The DOCX filename (e.g., "abc123.docx")
    #[serde(default)]
    pub docx_file: Option<String>,
    /// WAL entry count
    #[serde(alias = "wal_position", default)]
    pub wal_count: u64,
    /// Current cursor position in WAL
    #[serde(default)]
    pub cursor_position: u64,
    /// Checkpoint positions
    #[serde(default)]
    pub checkpoint_positions: Vec<u64>,
}

fn default_auto_sync() -> bool {
    true
}

/// Storage backend abstraction for tenant-aware document storage.
///
/// All methods take `tenant_id` as the first parameter to ensure isolation.
/// Implementations must organize data by tenant (e.g., `{base}/{tenant_id}/`).
#[async_trait]
pub trait StorageBackend: Send + Sync {
    /// Returns the backend identifier (e.g., "local", "r2").
    fn backend_name(&self) -> &'static str;

    // =========================================================================
    // Session Operations
    // =========================================================================

    /// Load a session's DOCX bytes.
    async fn load_session(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<Vec<u8>>, StorageError>;

    /// Save a session's DOCX bytes.
    async fn save_session(
        &self,
        tenant_id: &str,
        session_id: &str,
        data: &[u8],
    ) -> Result<(), StorageError>;

    /// Delete a session and all associated data (WAL, checkpoints).
    async fn delete_session(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<bool, StorageError>;

    /// List all sessions for a tenant.
    async fn list_sessions(&self, tenant_id: &str) -> Result<Vec<SessionInfo>, StorageError>;

    /// Check if a session exists.
    async fn session_exists(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<bool, StorageError>;

    // =========================================================================
    // Index Operations
    // =========================================================================

    /// Load the session index for a tenant.
    async fn load_index(&self, tenant_id: &str) -> Result<Option<SessionIndex>, StorageError>;

    /// Save the session index for a tenant.
    async fn save_index(
        &self,
        tenant_id: &str,
        index: &SessionIndex,
    ) -> Result<(), StorageError>;

    // =========================================================================
    // WAL Operations
    // =========================================================================

    /// Append entries to a session's WAL.
    async fn append_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        entries: &[WalEntry],
    ) -> Result<u64, StorageError>;

    /// Read WAL entries starting from a position.
    async fn read_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        from_position: u64,
        limit: Option<u64>,
    ) -> Result<(Vec<WalEntry>, bool), StorageError>;

    /// Truncate WAL, keeping only the first N entries.
    /// - keep_count = 0: delete all entries
    /// - keep_count = N: keep entries with position <= N
    async fn truncate_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        keep_count: u64,
    ) -> Result<u64, StorageError>;

    // =========================================================================
    // Checkpoint Operations
    // =========================================================================

    /// Save a checkpoint at a specific WAL position.
    async fn save_checkpoint(
        &self,
        tenant_id: &str,
        session_id: &str,
        position: u64,
        data: &[u8],
    ) -> Result<(), StorageError>;

    /// Load a checkpoint. If position is 0, load the latest.
    async fn load_checkpoint(
        &self,
        tenant_id: &str,
        session_id: &str,
        position: u64,
    ) -> Result<Option<(Vec<u8>, u64)>, StorageError>;

    /// List all checkpoints for a session.
    async fn list_checkpoints(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Vec<CheckpointInfo>, StorageError>;
}
