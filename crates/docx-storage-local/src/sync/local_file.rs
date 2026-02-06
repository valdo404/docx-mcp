use std::path::PathBuf;
use std::sync::Arc;

use async_trait::async_trait;
use dashmap::DashMap;
use docx_storage_core::{
    SourceDescriptor, SourceType, StorageBackend, StorageError, SyncBackend, SyncStatus,
};
use tokio::fs;
use tracing::{debug, instrument, warn};

/// Transient sync state (not persisted - only in memory during server lifetime)
#[derive(Debug, Clone, Default)]
struct TransientSyncState {
    last_synced_at: Option<i64>,
    has_pending_changes: bool,
    last_error: Option<String>,
}

/// Local file sync backend.
///
/// Handles syncing session data to local files (the original auto-save behavior).
/// Source path and auto_sync are persisted in the session index (index.json).
/// Transient state (last_synced_at, pending_changes, errors) is kept in memory.
pub struct LocalFileSyncBackend {
    /// Storage backend for reading/writing session index
    storage: Arc<dyn StorageBackend>,
    /// Transient state: (tenant_id, session_id) -> TransientSyncState
    transient: DashMap<(String, String), TransientSyncState>,
}

impl std::fmt::Debug for LocalFileSyncBackend {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LocalFileSyncBackend")
            .field("transient", &self.transient)
            .finish_non_exhaustive()
    }
}

impl LocalFileSyncBackend {
    /// Create a new LocalFileSyncBackend with a storage backend.
    pub fn new(storage: Arc<dyn StorageBackend>) -> Self {
        Self {
            storage,
            transient: DashMap::new(),
        }
    }

    /// Get the key for the transient state map.
    fn key(tenant_id: &str, session_id: &str) -> (String, String) {
        (tenant_id.to_string(), session_id.to_string())
    }

    /// Get the file path from a source descriptor.
    #[allow(dead_code)]
    fn get_file_path(source: &SourceDescriptor) -> Result<PathBuf, StorageError> {
        if source.source_type != SourceType::LocalFile {
            return Err(StorageError::Sync(format!(
                "LocalFileSyncBackend only supports LocalFile sources, got {:?}",
                source.source_type
            )));
        }
        Ok(PathBuf::from(&source.uri))
    }
}

#[async_trait]
impl SyncBackend for LocalFileSyncBackend {
    #[instrument(skip(self), level = "debug")]
    async fn register_source(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: SourceDescriptor,
        auto_sync: bool,
    ) -> Result<(), StorageError> {
        // Validate source type
        if source.source_type != SourceType::LocalFile {
            return Err(StorageError::Sync(format!(
                "LocalFileSyncBackend only supports LocalFile sources, got {:?}",
                source.source_type
            )));
        }

        // Load index, update entry, save index
        let mut index = self.storage.load_index(tenant_id).await?.unwrap_or_default();

        if let Some(entry) = index.get_mut(session_id) {
            entry.source_path = Some(source.uri.clone());
            entry.auto_sync = auto_sync;
            entry.last_modified_at = chrono::Utc::now();
        } else {
            return Err(StorageError::Sync(format!(
                "Session {} not found in index for tenant {}",
                session_id, tenant_id
            )));
        }

        self.storage.save_index(tenant_id, &index).await?;

        // Initialize transient state
        let key = Self::key(tenant_id, session_id);
        self.transient.insert(key, TransientSyncState::default());

        debug!(
            "Registered source for tenant {} session {} -> {} (auto_sync={})",
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
        // Load index, clear source_path, save index
        let mut index = self.storage.load_index(tenant_id).await?.unwrap_or_default();

        if let Some(entry) = index.get_mut(session_id) {
            entry.source_path = None;
            entry.auto_sync = false;
            entry.last_modified_at = chrono::Utc::now();
            self.storage.save_index(tenant_id, &index).await?;

            debug!(
                "Unregistered source for tenant {} session {}",
                tenant_id, session_id
            );
        }

        // Clear transient state
        let key = Self::key(tenant_id, session_id);
        self.transient.remove(&key);

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
        // Load index
        let mut index = self.storage.load_index(tenant_id).await?.unwrap_or_default();

        let entry = index.get_mut(session_id).ok_or_else(|| {
            StorageError::Sync(format!(
                "Session {} not found in index for tenant {}",
                session_id, tenant_id
            ))
        })?;

        // Check if source is registered
        if entry.source_path.is_none() {
            return Err(StorageError::Sync(format!(
                "No source registered for tenant {} session {}",
                tenant_id, session_id
            )));
        }

        // Update source if provided
        if let Some(new_source) = source {
            if new_source.source_type != SourceType::LocalFile {
                return Err(StorageError::Sync(format!(
                    "LocalFileSyncBackend only supports LocalFile sources, got {:?}",
                    new_source.source_type
                )));
            }
            debug!(
                "Updating source URI for tenant {} session {}: {:?} -> {}",
                tenant_id, session_id, entry.source_path, new_source.uri
            );
            entry.source_path = Some(new_source.uri);
        }

        // Update auto_sync if provided
        if let Some(new_auto_sync) = auto_sync {
            debug!(
                "Updating auto_sync for tenant {} session {}: {} -> {}",
                tenant_id, session_id, entry.auto_sync, new_auto_sync
            );
            entry.auto_sync = new_auto_sync;
        }

        entry.last_modified_at = chrono::Utc::now();
        self.storage.save_index(tenant_id, &index).await?;

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

        let source_path = entry.source_path.as_ref().ok_or_else(|| {
            StorageError::Sync(format!(
                "No source registered for tenant {} session {}",
                tenant_id, session_id
            ))
        })?;

        let file_path = PathBuf::from(source_path);

        // Ensure parent directory exists
        if let Some(parent) = file_path.parent() {
            fs::create_dir_all(parent).await.map_err(|e| {
                StorageError::Sync(format!(
                    "Failed to create parent directory for {}: {}",
                    file_path.display(),
                    e
                ))
            })?;
        }

        // Write atomically via temp file
        let temp_path = file_path.with_extension("docx.sync.tmp");
        fs::write(&temp_path, data).await.map_err(|e| {
            StorageError::Sync(format!(
                "Failed to write temp file {}: {}",
                temp_path.display(),
                e
            ))
        })?;

        fs::rename(&temp_path, &file_path).await.map_err(|e| {
            StorageError::Sync(format!(
                "Failed to rename temp file to {}: {}",
                file_path.display(),
                e
            ))
        })?;

        let synced_at = chrono::Utc::now().timestamp();

        // Update transient state
        let key = Self::key(tenant_id, session_id);
        self.transient
            .entry(key)
            .or_default()
            .last_synced_at = Some(synced_at);
        if let Some(mut state) = self.transient.get_mut(&Self::key(tenant_id, session_id)) {
            state.has_pending_changes = false;
            state.last_error = None;
        }

        debug!(
            "Synced {} bytes to {} for tenant {} session {}",
            data.len(),
            file_path.display(),
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

        // Get transient state
        let key = Self::key(tenant_id, session_id);
        let transient = self.transient.get(&key);

        Ok(Some(SyncStatus {
            session_id: session_id.to_string(),
            source: SourceDescriptor {
                source_type: SourceType::LocalFile,
                uri: source_path.clone(),
                metadata: Default::default(),
            },
            auto_sync_enabled: entry.auto_sync,
            last_synced_at: transient.as_ref().and_then(|t| t.last_synced_at),
            has_pending_changes: transient.as_ref().map(|t| t.has_pending_changes).unwrap_or(false),
            last_error: transient.as_ref().and_then(|t| t.last_error.clone()),
        }))
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_sources(&self, tenant_id: &str) -> Result<Vec<SyncStatus>, StorageError> {
        let index = self.storage.load_index(tenant_id).await?.unwrap_or_default();
        let mut results = Vec::new();

        for entry in &index.sessions {
            if let Some(source_path) = &entry.source_path {
                let key = Self::key(tenant_id, &entry.id);
                let transient = self.transient.get(&key);

                results.push(SyncStatus {
                    session_id: entry.id.clone(),
                    source: SourceDescriptor {
                        source_type: SourceType::LocalFile,
                        uri: source_path.clone(),
                        metadata: Default::default(),
                    },
                    auto_sync_enabled: entry.auto_sync,
                    last_synced_at: transient.as_ref().and_then(|t| t.last_synced_at),
                    has_pending_changes: transient.as_ref().map(|t| t.has_pending_changes).unwrap_or(false),
                    last_error: transient.as_ref().and_then(|t| t.last_error.clone()),
                });
            }
        }

        debug!(
            "Listed {} sources for tenant {}",
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
            .map(|e| e.source_path.is_some() && e.auto_sync)
            .unwrap_or(false))
    }
}

/// Mark a session as having pending changes (for auto-sync tracking).
impl LocalFileSyncBackend {
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::storage::LocalStorage;
    use tempfile::TempDir;

    async fn setup() -> (LocalFileSyncBackend, TempDir, TempDir) {
        let storage_dir = TempDir::new().unwrap();
        let output_dir = TempDir::new().unwrap();
        let storage = Arc::new(LocalStorage::new(storage_dir.path()));
        let backend = LocalFileSyncBackend::new(storage);
        (backend, storage_dir, output_dir)
    }

    async fn create_session(backend: &LocalFileSyncBackend, tenant: &str, session: &str) {
        // Create a session in the index
        let mut index = backend.storage.load_index(tenant).await.unwrap().unwrap_or_default();
        index.upsert(docx_storage_core::SessionIndexEntry {
            id: session.to_string(),
            source_path: None,
            auto_sync: false,
            created_at: chrono::Utc::now(),
            last_modified_at: chrono::Utc::now(),
            docx_file: None,
            wal_count: 0,
            cursor_position: 0,
            checkpoint_positions: vec![],
        });
        backend.storage.save_index(tenant, &index).await.unwrap();
    }

    #[tokio::test]
    async fn test_register_unregister() {
        let (backend, _storage_dir, output_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = output_dir.path().join("output.docx");

        // Create session first
        create_session(&backend, tenant, session).await;

        let source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: Default::default(),
        };

        // Register
        backend
            .register_source(tenant, session, source, true)
            .await
            .unwrap();

        // Check status
        let status = backend.get_sync_status(tenant, session).await.unwrap();
        assert!(status.is_some());
        let status = status.unwrap();
        assert!(status.auto_sync_enabled);
        assert!(status.last_synced_at.is_none());

        // Unregister
        backend.unregister_source(tenant, session).await.unwrap();

        // Check status again
        let status = backend.get_sync_status(tenant, session).await.unwrap();
        assert!(status.is_none());
    }

    #[tokio::test]
    async fn test_sync_to_source() {
        let (backend, _storage_dir, output_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = output_dir.path().join("output.docx");

        // Create session first
        create_session(&backend, tenant, session).await;

        let source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: Default::default(),
        };

        backend
            .register_source(tenant, session, source, true)
            .await
            .unwrap();

        // Sync data
        let data = b"PK\x03\x04fake docx content";
        let synced_at = backend.sync_to_source(tenant, session, data).await.unwrap();
        assert!(synced_at > 0);

        // Verify file was written
        let content = tokio::fs::read(&file_path).await.unwrap();
        assert_eq!(content, data);

        // Check status
        let status = backend
            .get_sync_status(tenant, session)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(status.last_synced_at, Some(synced_at));
        assert!(!status.has_pending_changes);
    }

    #[tokio::test]
    async fn test_list_sources() {
        let (backend, _storage_dir, output_dir) = setup().await;
        let tenant = "test-tenant";

        // Register multiple sources
        for i in 0..3 {
            let session = format!("session-{}", i);
            create_session(&backend, tenant, &session).await;

            let file_path = output_dir.path().join(format!("output-{}.docx", i));
            let source = SourceDescriptor {
                source_type: SourceType::LocalFile,
                uri: file_path.to_string_lossy().to_string(),
                metadata: Default::default(),
            };
            backend
                .register_source(tenant, &session, source, i % 2 == 0)
                .await
                .unwrap();
        }

        // List sources
        let sources = backend.list_sources(tenant).await.unwrap();
        assert_eq!(sources.len(), 3);

        // Different tenant should have empty list
        let other_sources = backend.list_sources("other-tenant").await.unwrap();
        assert!(other_sources.is_empty());
    }

    #[tokio::test]
    async fn test_pending_changes() {
        let (backend, _storage_dir, output_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = output_dir.path().join("output.docx");

        // Create session first
        create_session(&backend, tenant, session).await;

        let source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: Default::default(),
        };

        backend
            .register_source(tenant, session, source, true)
            .await
            .unwrap();

        // Initially no pending changes
        let status = backend
            .get_sync_status(tenant, session)
            .await
            .unwrap()
            .unwrap();
        assert!(!status.has_pending_changes);

        // Mark pending
        backend.mark_pending_changes(tenant, session);

        // Now has pending changes
        let status = backend
            .get_sync_status(tenant, session)
            .await
            .unwrap()
            .unwrap();
        assert!(status.has_pending_changes);

        // Sync clears pending
        let data = b"test";
        backend.sync_to_source(tenant, session, data).await.unwrap();

        let status = backend
            .get_sync_status(tenant, session)
            .await
            .unwrap()
            .unwrap();
        assert!(!status.has_pending_changes);
    }

    #[tokio::test]
    async fn test_invalid_source_type() {
        let (backend, _storage_dir, _output_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";

        // Create session first
        create_session(&backend, tenant, session).await;

        let source = SourceDescriptor {
            source_type: SourceType::S3,
            uri: "s3://bucket/key".to_string(),
            metadata: Default::default(),
        };

        let result = backend.register_source(tenant, session, source, true).await;
        assert!(result.is_err());
        assert!(result.unwrap_err().to_string().contains("LocalFile"));
    }

    #[tokio::test]
    async fn test_update_source() {
        let (backend, _storage_dir, output_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = output_dir.path().join("output.docx");
        let new_file_path = output_dir.path().join("new-output.docx");

        // Create session first
        create_session(&backend, tenant, session).await;

        let source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: Default::default(),
        };

        // Register source
        backend
            .register_source(tenant, session, source, true)
            .await
            .unwrap();

        // Verify initial state
        let status = backend.get_sync_status(tenant, session).await.unwrap().unwrap();
        assert_eq!(status.source.uri, file_path.to_string_lossy());
        assert!(status.auto_sync_enabled);

        // Update only auto_sync
        backend
            .update_source(tenant, session, None, Some(false))
            .await
            .unwrap();

        let status = backend.get_sync_status(tenant, session).await.unwrap().unwrap();
        assert_eq!(status.source.uri, file_path.to_string_lossy());
        assert!(!status.auto_sync_enabled);

        // Update source URI
        let new_source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: new_file_path.to_string_lossy().to_string(),
            metadata: Default::default(),
        };
        backend
            .update_source(tenant, session, Some(new_source), None)
            .await
            .unwrap();

        let status = backend.get_sync_status(tenant, session).await.unwrap().unwrap();
        assert_eq!(status.source.uri, new_file_path.to_string_lossy());
        assert!(!status.auto_sync_enabled); // Should remain unchanged

        // Update both
        let final_source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: Default::default(),
        };
        backend
            .update_source(tenant, session, Some(final_source), Some(true))
            .await
            .unwrap();

        let status = backend.get_sync_status(tenant, session).await.unwrap().unwrap();
        assert_eq!(status.source.uri, file_path.to_string_lossy());
        assert!(status.auto_sync_enabled);
    }

    #[tokio::test]
    async fn test_update_source_not_registered() {
        let (backend, _storage_dir, _output_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";

        // Create session but don't register source
        create_session(&backend, tenant, session).await;

        let result = backend.update_source(tenant, session, None, Some(true)).await;
        assert!(result.is_err());
        assert!(result.unwrap_err().to_string().contains("No source registered"));
    }
}
