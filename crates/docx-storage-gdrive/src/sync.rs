//! Google Drive SyncBackend implementation (multi-tenant).
//!
//! Resolves OAuth tokens per-connection via TokenManager.
//! URI format: `gdrive://{connection_id}/{file_id}`

use std::sync::Arc;

use async_trait::async_trait;
use dashmap::DashMap;
use docx_storage_core::{SourceDescriptor, SourceType, StorageError, SyncBackend, SyncStatus};
use tracing::{debug, instrument, warn};

use crate::gdrive::{parse_gdrive_uri, GDriveClient};
use crate::token_manager::TokenManager;

/// Transient sync state (in-memory only).
#[derive(Debug, Clone, Default)]
struct TransientSyncState {
    source: Option<SourceDescriptor>,
    auto_sync: bool,
    last_synced_at: Option<i64>,
    has_pending_changes: bool,
    last_error: Option<String>,
}

/// Google Drive sync backend (multi-tenant, token per-connection).
pub struct GDriveSyncBackend {
    client: Arc<GDriveClient>,
    token_manager: Arc<TokenManager>,
    /// Transient state: (tenant_id, session_id) -> TransientSyncState
    state: DashMap<(String, String), TransientSyncState>,
}

impl GDriveSyncBackend {
    pub fn new(client: Arc<GDriveClient>, token_manager: Arc<TokenManager>) -> Self {
        Self {
            client,
            token_manager,
            state: DashMap::new(),
        }
    }

    fn key(tenant_id: &str, session_id: &str) -> (String, String) {
        (tenant_id.to_string(), session_id.to_string())
    }
}

#[async_trait]
impl SyncBackend for GDriveSyncBackend {
    #[instrument(skip(self), level = "debug")]
    async fn register_source(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: SourceDescriptor,
        auto_sync: bool,
    ) -> Result<(), StorageError> {
        if source.source_type != SourceType::GoogleDrive {
            return Err(StorageError::Sync(format!(
                "GDriveSyncBackend only supports GoogleDrive sources, got {:?}",
                source.source_type
            )));
        }

        if parse_gdrive_uri(&source.uri).is_none() {
            return Err(StorageError::Sync(format!(
                "Invalid Google Drive URI: {}. Expected format: gdrive://{{connection_id}}/{{file_id}}",
                source.uri
            )));
        }

        let key = Self::key(tenant_id, session_id);
        self.state.insert(
            key,
            TransientSyncState {
                source: Some(source.clone()),
                auto_sync,
                ..Default::default()
            },
        );

        debug!(
            "Registered Google Drive source for tenant {} session {} -> {} (auto_sync={})",
            tenant_id, session_id, source.uri, auto_sync
        );

        Ok(())
    }

    #[instrument(skip(self), level = "debug")]
    async fn unregister_source(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<(), StorageError> {
        let key = Self::key(tenant_id, session_id);
        self.state.remove(&key);

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
        let key = Self::key(tenant_id, session_id);

        let mut entry = self.state.get_mut(&key).ok_or_else(|| {
            StorageError::Sync(format!(
                "No source registered for tenant {} session {}",
                tenant_id, session_id
            ))
        })?;

        if let Some(new_source) = source {
            if new_source.source_type != SourceType::GoogleDrive {
                return Err(StorageError::Sync(format!(
                    "GDriveSyncBackend only supports GoogleDrive sources, got {:?}",
                    new_source.source_type
                )));
            }
            entry.source = Some(new_source);
        }

        if let Some(new_auto_sync) = auto_sync {
            entry.auto_sync = new_auto_sync;
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
        let key = Self::key(tenant_id, session_id);

        let source_uri = {
            let entry = self.state.get(&key).ok_or_else(|| {
                StorageError::Sync(format!(
                    "No source registered for tenant {} session {}",
                    tenant_id, session_id
                ))
            })?;

            entry
                .source
                .as_ref()
                .map(|s| s.uri.clone())
                .ok_or_else(|| {
                    StorageError::Sync(format!(
                        "No source configured for tenant {} session {}",
                        tenant_id, session_id
                    ))
                })?
        };

        let parsed = parse_gdrive_uri(&source_uri).ok_or_else(|| {
            StorageError::Sync(format!("Invalid Google Drive URI: {}", source_uri))
        })?;

        // Get a valid token for this connection (tenant-scoped)
        let token = self
            .token_manager
            .get_valid_token(tenant_id, &parsed.connection_id)
            .await
            .map_err(|e| StorageError::Sync(format!("Token error: {}", e)))?;

        self.client
            .update_file(&token, &parsed.file_id, data)
            .await
            .map_err(|e| StorageError::Sync(format!("Google Drive upload failed: {}", e)))?;

        let synced_at = chrono::Utc::now().timestamp();

        // Update transient state
        if let Some(mut entry) = self.state.get_mut(&key) {
            entry.last_synced_at = Some(synced_at);
            entry.has_pending_changes = false;
            entry.last_error = None;
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
        let key = Self::key(tenant_id, session_id);

        let entry = match self.state.get(&key) {
            Some(e) => e,
            None => return Ok(None),
        };

        let source = match &entry.source {
            Some(s) => s.clone(),
            None => return Ok(None),
        };

        Ok(Some(SyncStatus {
            session_id: session_id.to_string(),
            source,
            auto_sync_enabled: entry.auto_sync,
            last_synced_at: entry.last_synced_at,
            has_pending_changes: entry.has_pending_changes,
            last_error: entry.last_error.clone(),
        }))
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_sources(&self, tenant_id: &str) -> Result<Vec<SyncStatus>, StorageError> {
        let mut results = Vec::new();

        for entry in self.state.iter() {
            let (key_tenant, _) = entry.key();
            if key_tenant != tenant_id {
                continue;
            }

            let state = entry.value();
            if let Some(source) = &state.source {
                let (_, session_id) = entry.key();
                results.push(SyncStatus {
                    session_id: session_id.clone(),
                    source: source.clone(),
                    auto_sync_enabled: state.auto_sync,
                    last_synced_at: state.last_synced_at,
                    has_pending_changes: state.has_pending_changes,
                    last_error: state.last_error.clone(),
                });
            }
        }

        debug!(
            "Listed {} Google Drive sources for tenant {}",
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
        let key = Self::key(tenant_id, session_id);
        Ok(self
            .state
            .get(&key)
            .map(|e| e.auto_sync && e.source.is_some())
            .unwrap_or(false))
    }
}

impl GDriveSyncBackend {
    #[allow(dead_code)]
    pub fn mark_pending_changes(&self, tenant_id: &str, session_id: &str) {
        let key = Self::key(tenant_id, session_id);
        if let Some(mut state) = self.state.get_mut(&key) {
            state.has_pending_changes = true;
        }
    }

    #[allow(dead_code)]
    pub fn record_sync_error(&self, tenant_id: &str, session_id: &str, error: &str) {
        let key = Self::key(tenant_id, session_id);
        if let Some(mut state) = self.state.get_mut(&key) {
            state.last_error = Some(error.to_string());
            warn!(
                "Sync error for tenant {} session {}: {}",
                tenant_id, session_id, error
            );
        }
    }
}
