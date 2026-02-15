use async_trait::async_trait;
use serde::{Deserialize, Serialize};

use crate::error::StorageError;
use crate::sync::SourceDescriptor;

/// Types of external changes that can be detected.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ExternalChangeType {
    Modified,
    Deleted,
    Renamed,
    PermissionChanged,
}

/// Metadata about a source file for comparison.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SourceMetadata {
    /// File size in bytes
    pub size_bytes: u64,
    /// Last modification time (Unix timestamp)
    pub modified_at: i64,
    /// ETag for HTTP-based sources
    pub etag: Option<String>,
    /// Version ID for versioned sources (S3, SharePoint)
    pub version_id: Option<String>,
    /// SHA-256 content hash (if available)
    pub content_hash: Option<Vec<u8>>,
}

/// Event representing an external change to a source.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ExternalChangeEvent {
    /// Session ID affected
    pub session_id: String,
    /// Type of change
    pub change_type: ExternalChangeType,
    /// Previous metadata (if known)
    pub old_metadata: Option<SourceMetadata>,
    /// New metadata
    pub new_metadata: Option<SourceMetadata>,
    /// Unix timestamp when change was detected
    pub detected_at: i64,
    /// New URI for rename events
    pub new_uri: Option<String>,
}

/// Watch backend abstraction for monitoring external sources for changes.
///
/// This is used to detect when external sources are modified outside of docx-mcp,
/// enabling conflict detection and re-sync notifications.
///
/// Different implementations support different mechanisms:
/// - Local files: `notify` crate for filesystem events
/// - R2/S3: Polling-based change detection
/// - SharePoint/OneDrive: Webhooks or polling
#[async_trait]
pub trait WatchBackend: Send + Sync {
    /// Start watching a source for external changes.
    ///
    /// # Arguments
    /// * `tenant_id` - Tenant identifier
    /// * `session_id` - Session identifier
    /// * `source` - Source descriptor
    /// * `poll_interval_secs` - Polling interval for backends that don't support push (0 = default)
    ///
    /// # Returns
    /// Unique watch ID for this session
    async fn start_watch(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: &SourceDescriptor,
        poll_interval_secs: u32,
    ) -> Result<String, StorageError>;

    /// Stop watching a source.
    async fn stop_watch(&self, tenant_id: &str, session_id: &str) -> Result<(), StorageError>;

    /// Poll for changes (for backends that don't support push notifications).
    ///
    /// Returns `Some(event)` if a change was detected, `None` otherwise.
    async fn check_for_changes(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<ExternalChangeEvent>, StorageError>;

    /// Get current source metadata (for comparison).
    async fn get_source_metadata(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<SourceMetadata>, StorageError>;

    /// Get known (cached) metadata for a session.
    async fn get_known_metadata(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<SourceMetadata>, StorageError>;

    /// Update known metadata after a successful sync.
    async fn update_known_metadata(
        &self,
        tenant_id: &str,
        session_id: &str,
        metadata: SourceMetadata,
    ) -> Result<(), StorageError>;
}
