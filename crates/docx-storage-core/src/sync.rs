use std::collections::HashMap;

use async_trait::async_trait;
use serde::{Deserialize, Serialize};

use crate::error::StorageError;

/// Source types supported by the sync service.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SourceType {
    LocalFile,
    SharePoint,
    OneDrive,
    S3,
    R2,
}

impl Default for SourceType {
    fn default() -> Self {
        Self::LocalFile
    }
}

/// Descriptor for an external source.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SourceDescriptor {
    /// Type of the source
    #[serde(rename = "type")]
    pub source_type: SourceType,
    /// URI of the source (file path, URL, S3 URI, etc.)
    pub uri: String,
    /// Type-specific metadata (credentials ref, etc.)
    #[serde(default)]
    pub metadata: HashMap<String, String>,
}

/// Status of sync for a session.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncStatus {
    /// Session ID
    pub session_id: String,
    /// Source descriptor
    pub source: SourceDescriptor,
    /// Whether auto-sync is enabled
    pub auto_sync_enabled: bool,
    /// Unix timestamp of last successful sync
    pub last_synced_at: Option<i64>,
    /// Whether there are pending changes not yet synced
    pub has_pending_changes: bool,
    /// Last error message, if any
    pub last_error: Option<String>,
}

/// Sync backend abstraction for syncing session changes to external sources.
///
/// This handles the auto-save functionality for various source types:
/// - Local files (current behavior)
/// - SharePoint documents
/// - OneDrive files
/// - S3/R2 objects
#[async_trait]
pub trait SyncBackend: Send + Sync {
    /// Register a session's source for sync tracking.
    ///
    /// # Arguments
    /// * `tenant_id` - Tenant identifier
    /// * `session_id` - Session identifier
    /// * `source` - Source descriptor
    /// * `auto_sync` - Whether to enable auto-sync on WAL append
    async fn register_source(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: SourceDescriptor,
        auto_sync: bool,
    ) -> Result<(), StorageError>;

    /// Unregister a source (on session close).
    async fn unregister_source(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<(), StorageError>;

    /// Update source configuration (change target file, toggle auto-sync).
    ///
    /// # Arguments
    /// * `tenant_id` - Tenant identifier
    /// * `session_id` - Session identifier
    /// * `source` - New source descriptor (None to keep existing)
    /// * `auto_sync` - New auto-sync setting (None to keep existing)
    async fn update_source(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: Option<SourceDescriptor>,
        auto_sync: Option<bool>,
    ) -> Result<(), StorageError>;

    /// Sync current document data to the external source.
    ///
    /// # Arguments
    /// * `tenant_id` - Tenant identifier
    /// * `session_id` - Session identifier
    /// * `data` - DOCX bytes to sync
    ///
    /// # Returns
    /// Unix timestamp of successful sync
    async fn sync_to_source(
        &self,
        tenant_id: &str,
        session_id: &str,
        data: &[u8],
    ) -> Result<i64, StorageError>;

    /// Get sync status for a session.
    async fn get_sync_status(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<SyncStatus>, StorageError>;

    /// List all registered sources for a tenant.
    async fn list_sources(&self, tenant_id: &str) -> Result<Vec<SyncStatus>, StorageError>;

    /// Check if auto-sync is enabled for a session.
    async fn is_auto_sync_enabled(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<bool, StorageError>;
}
