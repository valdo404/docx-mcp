//! Google Drive WatchBackend implementation (multi-tenant).
//!
//! Polling-based change detection using `headRevisionId` from Drive API.
//! Resolves OAuth tokens per-connection via TokenManager.

use async_trait::async_trait;
use dashmap::DashMap;
use docx_storage_core::{
    ExternalChangeEvent, ExternalChangeType, SourceDescriptor, SourceMetadata, SourceType,
    StorageError, WatchBackend,
};
use std::sync::Arc;
use tracing::{debug, instrument};

use crate::gdrive::GDriveClient;
use crate::token_manager::TokenManager;

/// State for a watched Google Drive file.
#[derive(Debug, Clone)]
struct WatchedSource {
    source: SourceDescriptor,
    #[allow(dead_code)]
    watch_id: String,
    known_metadata: Option<SourceMetadata>,
    poll_interval_secs: u32,
}

/// Polling-based watch backend for Google Drive (multi-tenant).
pub struct GDriveWatchBackend {
    client: Arc<GDriveClient>,
    token_manager: Arc<TokenManager>,
    /// Watched sources: (tenant_id, session_id) -> WatchedSource
    sources: DashMap<(String, String), WatchedSource>,
    /// Pending change events
    pending_changes: DashMap<(String, String), ExternalChangeEvent>,
    /// Default poll interval (seconds)
    default_poll_interval: u32,
}

impl GDriveWatchBackend {
    pub fn new(
        client: Arc<GDriveClient>,
        token_manager: Arc<TokenManager>,
        default_poll_interval: u32,
    ) -> Self {
        Self {
            client,
            token_manager,
            sources: DashMap::new(),
            pending_changes: DashMap::new(),
            default_poll_interval,
        }
    }

    fn key(tenant_id: &str, session_id: &str) -> (String, String) {
        (tenant_id.to_string(), session_id.to_string())
    }

    /// Fetch metadata from Google Drive and convert to SourceMetadata.
    async fn fetch_metadata(
        &self,
        token: &str,
        file_id: &str,
    ) -> Result<Option<SourceMetadata>, StorageError> {
        let metadata = self
            .client
            .get_metadata(token, file_id)
            .await
            .map_err(|e| StorageError::Watch(format!("Google Drive API error: {}", e)))?;

        Ok(metadata.map(|m| {
            let size_bytes = m
                .size
                .as_ref()
                .and_then(|s| s.parse::<u64>().ok())
                .unwrap_or(0);

            let modified_at = m
                .modified_time
                .as_ref()
                .and_then(|t| chrono::DateTime::parse_from_rfc3339(t).ok())
                .map(|dt| dt.timestamp())
                .unwrap_or(0);

            let content_hash = m
                .md5_checksum
                .as_ref()
                .and_then(|h| hex::decode(h).ok());

            SourceMetadata {
                size_bytes,
                modified_at,
                etag: None,
                version_id: m.head_revision_id.clone(),
                content_hash,
            }
        }))
    }

    /// Get a valid token for a source, using its connection_id (tenant-scoped).
    async fn get_token_for_source(
        &self,
        tenant_id: &str,
        source: &SourceDescriptor,
    ) -> Result<(String, String), StorageError> {
        let connection_id = source.connection_id.as_deref().ok_or_else(|| {
            StorageError::Watch("Google Drive source requires a connection_id".to_string())
        })?;

        let token = self
            .token_manager
            .get_valid_token(tenant_id, connection_id)
            .await
            .map_err(|e| StorageError::Watch(format!("Token error: {}", e)))?;

        let file_id = source.effective_id().to_string();
        Ok((token, file_id))
    }

    /// Compare metadata to detect changes. Prefers headRevisionId.
    fn has_changed(old: &SourceMetadata, new: &SourceMetadata) -> bool {
        // Prefer headRevisionId comparison (most reliable for Google Drive)
        if let (Some(old_ver), Some(new_ver)) = (&old.version_id, &new.version_id) {
            return old_ver != new_ver;
        }

        // Fall back to content hash (md5Checksum)
        if let (Some(old_hash), Some(new_hash)) = (&old.content_hash, &new.content_hash) {
            return old_hash != new_hash;
        }

        // Last resort: size and mtime
        old.size_bytes != new.size_bytes || old.modified_at != new.modified_at
    }

    /// Get the configured poll interval for a watched source.
    pub fn get_poll_interval(&self, tenant_id: &str, session_id: &str) -> u32 {
        let key = Self::key(tenant_id, session_id);
        self.sources
            .get(&key)
            .map(|w| w.poll_interval_secs)
            .unwrap_or(self.default_poll_interval)
    }
}

#[async_trait]
impl WatchBackend for GDriveWatchBackend {
    #[instrument(skip(self), level = "debug")]
    async fn start_watch(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: &SourceDescriptor,
        poll_interval_secs: u32,
    ) -> Result<String, StorageError> {
        if source.source_type != SourceType::GoogleDrive {
            return Err(StorageError::Watch(format!(
                "GDriveWatchBackend only supports GoogleDrive sources, got {:?}",
                source.source_type
            )));
        }

        let (token, file_id) = self.get_token_for_source(tenant_id, source).await?;

        let watch_id = uuid::Uuid::new_v4().to_string();
        let map_key = Self::key(tenant_id, session_id);

        // Get initial metadata
        let known_metadata = self.fetch_metadata(&token, &file_id).await?;

        let poll_interval = if poll_interval_secs > 0 {
            poll_interval_secs
        } else {
            self.default_poll_interval
        };

        self.sources.insert(
            map_key,
            WatchedSource {
                source: source.clone(),
                watch_id: watch_id.clone(),
                known_metadata,
                poll_interval_secs: poll_interval,
            },
        );

        debug!(
            "Started watching Google Drive file {} (tenant {} session {}, interval {} secs)",
            file_id, tenant_id, session_id, poll_interval
        );

        Ok(watch_id)
    }

    #[instrument(skip(self), level = "debug")]
    async fn stop_watch(&self, tenant_id: &str, session_id: &str) -> Result<(), StorageError> {
        let key = Self::key(tenant_id, session_id);

        if let Some((_, watched)) = self.sources.remove(&key) {
            debug!(
                "Stopped watching {} for tenant {} session {}",
                watched.source.effective_id(),
                tenant_id,
                session_id
            );
        }

        self.pending_changes.remove(&key);
        Ok(())
    }

    #[instrument(skip(self), level = "debug")]
    async fn check_for_changes(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<ExternalChangeEvent>, StorageError> {
        let key = Self::key(tenant_id, session_id);

        // Check for pending changes first
        if let Some((_, event)) = self.pending_changes.remove(&key) {
            return Ok(Some(event));
        }

        // Get watched source
        let watched = match self.sources.get(&key) {
            Some(w) => w.clone(),
            None => return Ok(None),
        };

        let (token, file_id) = self.get_token_for_source(tenant_id, &watched.source).await?;

        // Get current metadata
        let current_metadata = match self.fetch_metadata(&token, &file_id).await? {
            Some(m) => m,
            None => {
                // File was deleted
                if watched.known_metadata.is_some() {
                    let event = ExternalChangeEvent {
                        session_id: session_id.to_string(),
                        change_type: ExternalChangeType::Deleted,
                        old_metadata: watched.known_metadata.clone(),
                        new_metadata: None,
                        detected_at: chrono::Utc::now().timestamp(),
                        new_uri: None,
                    };
                    return Ok(Some(event));
                }
                return Ok(None);
            }
        };

        // Compare with known metadata
        if let Some(known) = &watched.known_metadata {
            if Self::has_changed(known, &current_metadata) {
                debug!(
                    "Detected change in {} (revision: {:?} -> {:?})",
                    watched.source.effective_id(),
                    known.version_id,
                    current_metadata.version_id
                );

                let event = ExternalChangeEvent {
                    session_id: session_id.to_string(),
                    change_type: ExternalChangeType::Modified,
                    old_metadata: Some(known.clone()),
                    new_metadata: Some(current_metadata),
                    detected_at: chrono::Utc::now().timestamp(),
                    new_uri: None,
                };

                return Ok(Some(event));
            }
        }

        Ok(None)
    }

    #[instrument(skip(self), level = "debug")]
    async fn get_source_metadata(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<SourceMetadata>, StorageError> {
        let key = Self::key(tenant_id, session_id);

        let watched = match self.sources.get(&key) {
            Some(w) => w.clone(),
            None => return Ok(None),
        };

        let (token, file_id) = self.get_token_for_source(tenant_id, &watched.source).await?;
        self.fetch_metadata(&token, &file_id).await
    }

    #[instrument(skip(self), level = "debug")]
    async fn get_known_metadata(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<SourceMetadata>, StorageError> {
        let key = Self::key(tenant_id, session_id);

        Ok(self
            .sources
            .get(&key)
            .and_then(|w| w.known_metadata.clone()))
    }

    #[instrument(skip(self, metadata), level = "debug")]
    async fn update_known_metadata(
        &self,
        tenant_id: &str,
        session_id: &str,
        metadata: SourceMetadata,
    ) -> Result<(), StorageError> {
        let key = Self::key(tenant_id, session_id);

        if let Some(mut watched) = self.sources.get_mut(&key) {
            watched.known_metadata = Some(metadata);
            debug!(
                "Updated known metadata for tenant {} session {}",
                tenant_id, session_id
            );
        }

        Ok(())
    }
}
