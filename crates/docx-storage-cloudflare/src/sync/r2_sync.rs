use std::sync::Arc;

use async_trait::async_trait;
use aws_sdk_s3::primitives::ByteStream;
use aws_sdk_s3::Client as S3Client;
use dashmap::DashMap;
use docx_storage_core::{
    SourceDescriptor, SourceType, StorageBackend, StorageError, SyncBackend, SyncStatus,
};
use tracing::{debug, instrument, warn};

use crate::storage::R2Storage;

/// Transient sync state (not persisted - only in memory during server lifetime)
#[derive(Debug, Clone, Default)]
struct TransientSyncState {
    last_synced_at: Option<i64>,
    has_pending_changes: bool,
    last_error: Option<String>,
}

/// R2 sync backend.
///
/// Handles syncing session data to R2 buckets. Supports both internal R2 buckets
/// and external S3-compatible storage.
///
/// Source path and auto_sync are persisted in the session index.
/// Transient state (last_synced_at, pending_changes, errors) is kept in memory.
pub struct R2SyncBackend {
    /// S3 client for R2 operations
    s3_client: S3Client,
    /// Default bucket for R2 sources
    default_bucket: String,
    /// Storage backend for reading/writing session index (concrete R2Storage for CAS)
    storage: Arc<R2Storage>,
    /// Transient state: (tenant_id, session_id) -> TransientSyncState
    transient: DashMap<(String, String), TransientSyncState>,
}

impl std::fmt::Debug for R2SyncBackend {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("R2SyncBackend")
            .field("default_bucket", &self.default_bucket)
            .field("transient", &self.transient)
            .finish_non_exhaustive()
    }
}

impl R2SyncBackend {
    /// Create a new R2SyncBackend.
    pub fn new(
        s3_client: S3Client,
        default_bucket: String,
        storage: Arc<R2Storage>,
    ) -> Self {
        Self {
            s3_client,
            default_bucket,
            storage,
            transient: DashMap::new(),
        }
    }

    /// Get the key for the transient state map.
    fn key(tenant_id: &str, session_id: &str) -> (String, String) {
        (tenant_id.to_string(), session_id.to_string())
    }

    /// Parse R2/S3 URI into bucket and key.
    /// Supports formats:
    /// - r2://bucket/key
    /// - s3://bucket/key
    fn parse_uri(uri: &str) -> Option<(String, String)> {
        let uri = uri
            .strip_prefix("r2://")
            .or_else(|| uri.strip_prefix("s3://"))?;

        let mut parts = uri.splitn(2, '/');
        let bucket = parts.next()?.to_string();
        let key = parts.next().unwrap_or("").to_string();
        Some((bucket, key))
    }
}

#[async_trait]
impl SyncBackend for R2SyncBackend {
    #[instrument(skip(self), level = "debug")]
    async fn register_source(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: SourceDescriptor,
        auto_sync: bool,
    ) -> Result<(), StorageError> {
        // Validate source type
        if source.source_type != SourceType::R2 && source.source_type != SourceType::S3 {
            return Err(StorageError::Sync(format!(
                "R2SyncBackend only supports R2/S3 sources, got {:?}",
                source.source_type
            )));
        }

        // Validate URI format
        if Self::parse_uri(&source.uri).is_none() {
            return Err(StorageError::Sync(format!(
                "Invalid R2/S3 URI: {}. Expected format: r2://bucket/key or s3://bucket/key",
                source.uri
            )));
        }

        let sid = session_id.to_string();
        let tid = tenant_id.to_string();
        let source_uri = source.uri.clone();

        self.storage
            .cas_index(tenant_id, |index| {
                if let Some(entry) = index.get_mut(&sid) {
                    entry.source_path = Some(source_uri.clone());
                    entry.auto_sync = auto_sync;
                    entry.last_modified_at = chrono::Utc::now();
                }
            })
            .await?;

        // Check if session was found by loading index to verify
        let index = self.storage.load_index(tenant_id).await?;
        if let Some(idx) = &index {
            if idx.get(session_id).is_none() {
                return Err(StorageError::Sync(format!(
                    "Session {} not found in index for tenant {}",
                    session_id, tenant_id
                )));
            }
        } else {
            return Err(StorageError::Sync(format!(
                "Session {} not found in index for tenant {}",
                session_id, tenant_id
            )));
        }

        // Initialize transient state
        let key = Self::key(tenant_id, session_id);
        self.transient.insert(key, TransientSyncState::default());

        debug!(
            "Registered R2 source for tenant {} session {} -> {} (auto_sync={})",
            tid, sid, source.uri, auto_sync
        );

        Ok(())
    }

    #[instrument(skip(self), level = "debug")]
    async fn unregister_source(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<(), StorageError> {
        let sid = session_id.to_string();

        self.storage
            .cas_index(tenant_id, |index| {
                if let Some(entry) = index.get_mut(&sid) {
                    entry.source_path = None;
                    entry.auto_sync = false;
                    entry.last_modified_at = chrono::Utc::now();
                }
            })
            .await?;

        // Clear transient state
        let key = Self::key(tenant_id, session_id);
        self.transient.remove(&key);

        debug!(
            "Unregistered source for tenant {} session {}",
            tenant_id, session_id
        );

        Ok(())
    }

    #[instrument(skip(self), level = "debug")]
    async fn update_source(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: Option<SourceDescriptor>,
        auto_sync: Option<bool>,
    ) -> Result<(), StorageError> {
        // Validate new source if provided
        if let Some(ref new_source) = source {
            if new_source.source_type != SourceType::R2 && new_source.source_type != SourceType::S3
            {
                return Err(StorageError::Sync(format!(
                    "R2SyncBackend only supports R2/S3 sources, got {:?}",
                    new_source.source_type
                )));
            }
            if Self::parse_uri(&new_source.uri).is_none() {
                return Err(StorageError::Sync(format!(
                    "Invalid R2/S3 URI: {}",
                    new_source.uri
                )));
            }
        }

        let sid = session_id.to_string();
        let new_uri = source.map(|s| s.uri);
        let mut not_found = false;
        let mut no_source = false;

        self.storage
            .cas_index(tenant_id, |index| {
                let entry = match index.get_mut(&sid) {
                    Some(e) => e,
                    None => {
                        not_found = true;
                        return;
                    }
                };
                not_found = false;

                if entry.source_path.is_none() {
                    no_source = true;
                    return;
                }
                no_source = false;

                if let Some(ref uri) = new_uri {
                    entry.source_path = Some(uri.clone());
                }
                if let Some(new_auto_sync) = auto_sync {
                    entry.auto_sync = new_auto_sync;
                }
                entry.last_modified_at = chrono::Utc::now();
            })
            .await?;

        if not_found {
            return Err(StorageError::Sync(format!(
                "Session {} not found in index for tenant {}",
                session_id, tenant_id
            )));
        }
        if no_source {
            return Err(StorageError::Sync(format!(
                "No source registered for tenant {} session {}",
                tenant_id, session_id
            )));
        }

        Ok(())
    }

    #[instrument(skip(self, data), level = "debug", fields(data_len = data.len()))]
    async fn sync_to_source(
        &self,
        tenant_id: &str,
        session_id: &str,
        data: &[u8],
    ) -> Result<i64, StorageError> {
        // Get source path from index
        let index = self.storage.load_index(tenant_id).await?.unwrap_or_default();

        let entry = index.get(session_id).ok_or_else(|| {
            StorageError::Sync(format!(
                "Session {} not found in index for tenant {}",
                session_id, tenant_id
            ))
        })?;

        let source_uri = entry.source_path.as_ref().ok_or_else(|| {
            StorageError::Sync(format!(
                "No source registered for tenant {} session {}",
                tenant_id, session_id
            ))
        })?;

        let (bucket, key) = Self::parse_uri(source_uri).ok_or_else(|| {
            StorageError::Sync(format!("Invalid R2/S3 URI: {}", source_uri))
        })?;

        // Use default bucket if key is just a path
        let bucket = if bucket.is_empty() {
            self.default_bucket.clone()
        } else {
            bucket
        };

        // Upload to R2
        self.s3_client
            .put_object()
            .bucket(&bucket)
            .key(&key)
            .body(ByteStream::from(data.to_vec()))
            .send()
            .await
            .map_err(|e| StorageError::Sync(format!("Failed to upload to R2: {}", e)))?;

        let synced_at = chrono::Utc::now().timestamp();

        // Update transient state
        let state_key = Self::key(tenant_id, session_id);
        self.transient
            .entry(state_key)
            .or_default()
            .last_synced_at = Some(synced_at);
        if let Some(mut state) = self.transient.get_mut(&Self::key(tenant_id, session_id)) {
            state.has_pending_changes = false;
            state.last_error = None;
        }

        debug!(
            "Synced {} bytes to {} for tenant {} session {}",
            data.len(),
            source_uri,
            tenant_id,
            session_id
        );

        Ok(synced_at)
    }

    #[instrument(skip(self), level = "debug")]
    async fn get_sync_status(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<SyncStatus>, StorageError> {
        // Get source info from index
        let index = self.storage.load_index(tenant_id).await?.unwrap_or_default();

        let entry = match index.get(session_id) {
            Some(e) => e,
            None => return Ok(None),
        };

        let source_path = match &entry.source_path {
            Some(p) => p,
            None => return Ok(None),
        };

        // Determine source type from URI
        let source_type = if source_path.starts_with("r2://") {
            SourceType::R2
        } else {
            SourceType::S3
        };

        // Get transient state
        let key = Self::key(tenant_id, session_id);
        let transient = self.transient.get(&key);

        Ok(Some(SyncStatus {
            session_id: session_id.to_string(),
            source: SourceDescriptor {
                source_type,
                uri: source_path.clone(),
                metadata: Default::default(),
            },
            auto_sync_enabled: entry.auto_sync,
            last_synced_at: transient.as_ref().and_then(|t| t.last_synced_at),
            has_pending_changes: transient
                .as_ref()
                .map(|t| t.has_pending_changes)
                .unwrap_or(false),
            last_error: transient.as_ref().and_then(|t| t.last_error.clone()),
        }))
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_sources(&self, tenant_id: &str) -> Result<Vec<SyncStatus>, StorageError> {
        let index = self.storage.load_index(tenant_id).await?.unwrap_or_default();
        let mut results = Vec::new();

        for entry in &index.sessions {
            if let Some(source_path) = &entry.source_path {
                // Only include R2/S3 sources
                if source_path.starts_with("r2://") || source_path.starts_with("s3://") {
                    let source_type = if source_path.starts_with("r2://") {
                        SourceType::R2
                    } else {
                        SourceType::S3
                    };

                    let key = Self::key(tenant_id, &entry.id);
                    let transient = self.transient.get(&key);

                    results.push(SyncStatus {
                        session_id: entry.id.clone(),
                        source: SourceDescriptor {
                            source_type,
                            uri: source_path.clone(),
                            metadata: Default::default(),
                        },
                        auto_sync_enabled: entry.auto_sync,
                        last_synced_at: transient.as_ref().and_then(|t| t.last_synced_at),
                        has_pending_changes: transient
                            .as_ref()
                            .map(|t| t.has_pending_changes)
                            .unwrap_or(false),
                        last_error: transient.as_ref().and_then(|t| t.last_error.clone()),
                    });
                }
            }
        }

        debug!(
            "Listed {} R2/S3 sources for tenant {}",
            results.len(),
            tenant_id
        );
        Ok(results)
    }

    #[instrument(skip(self), level = "debug")]
    async fn is_auto_sync_enabled(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<bool, StorageError> {
        let index = self.storage.load_index(tenant_id).await?.unwrap_or_default();

        Ok(index
            .get(session_id)
            .map(|e| {
                e.source_path.as_ref().map_or(false, |p| {
                    (p.starts_with("r2://") || p.starts_with("s3://")) && e.auto_sync
                })
            })
            .unwrap_or(false))
    }
}

/// Mark a session as having pending changes.
impl R2SyncBackend {
    #[allow(dead_code)]
    pub fn mark_pending_changes(&self, tenant_id: &str, session_id: &str) {
        let key = Self::key(tenant_id, session_id);
        self.transient
            .entry(key)
            .or_default()
            .has_pending_changes = true;
    }

    #[allow(dead_code)]
    pub fn record_sync_error(&self, tenant_id: &str, session_id: &str, error: &str) {
        let key = Self::key(tenant_id, session_id);
        if let Some(mut state) = self.transient.get_mut(&key) {
            state.last_error = Some(error.to_string());
            warn!(
                "Sync error for tenant {} session {}: {}",
                tenant_id, session_id, error
            );
        }
    }
}
