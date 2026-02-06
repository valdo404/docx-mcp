use std::path::PathBuf;

use async_trait::async_trait;
use dashmap::DashMap;
use docx_storage_core::{
    SourceDescriptor, SourceType, StorageError, SyncBackend, SyncStatus,
};
use tokio::fs;
use tracing::{debug, instrument, warn};

/// State for a registered source
#[derive(Debug, Clone)]
struct RegisteredSource {
    source: SourceDescriptor,
    auto_sync: bool,
    last_synced_at: Option<i64>,
    has_pending_changes: bool,
    last_error: Option<String>,
}

/// Local file sync backend.
///
/// Handles syncing session data to local files (the original auto-save behavior).
/// Data is organized by tenant:
/// ```
/// Source registry is stored in memory.
/// The actual sync writes directly to the source URI (file path).
/// ```
#[derive(Debug)]
pub struct LocalFileSyncBackend {
    /// Registered sources: (tenant_id, session_id) -> RegisteredSource
    sources: DashMap<(String, String), RegisteredSource>,
}

impl Default for LocalFileSyncBackend {
    fn default() -> Self {
        Self::new()
    }
}

impl LocalFileSyncBackend {
    /// Create a new LocalFileSyncBackend.
    pub fn new() -> Self {
        Self {
            sources: DashMap::new(),
        }
    }

    /// Get the key for the sources map.
    fn key(tenant_id: &str, session_id: &str) -> (String, String) {
        (tenant_id.to_string(), session_id.to_string())
    }

    /// Get the file path from a source descriptor.
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

        let key = Self::key(tenant_id, session_id);
        let registered = RegisteredSource {
            source,
            auto_sync,
            last_synced_at: None,
            has_pending_changes: false,
            last_error: None,
        };

        self.sources.insert(key, registered);
        debug!(
            "Registered source for tenant {} session {} (auto_sync={})",
            tenant_id, session_id, auto_sync
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
        if self.sources.remove(&key).is_some() {
            debug!(
                "Unregistered source for tenant {} session {}",
                tenant_id, session_id
            );
        }
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

        let mut entry = self.sources.get_mut(&key).ok_or_else(|| {
            StorageError::Sync(format!(
                "No source registered for tenant {} session {}",
                tenant_id, session_id
            ))
        })?;

        // Update source if provided
        if let Some(new_source) = source {
            // Validate source type
            if new_source.source_type != SourceType::LocalFile {
                return Err(StorageError::Sync(format!(
                    "LocalFileSyncBackend only supports LocalFile sources, got {:?}",
                    new_source.source_type
                )));
            }
            debug!(
                "Updating source URI for tenant {} session {}: {} -> {}",
                tenant_id, session_id, entry.source.uri, new_source.uri
            );
            entry.source = new_source;
        }

        // Update auto_sync if provided
        if let Some(new_auto_sync) = auto_sync {
            debug!(
                "Updating auto_sync for tenant {} session {}: {} -> {}",
                tenant_id, session_id, entry.auto_sync, new_auto_sync
            );
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

        let source = self
            .sources
            .get(&key)
            .ok_or_else(|| {
                StorageError::Sync(format!(
                    "No source registered for tenant {} session {}",
                    tenant_id, session_id
                ))
            })?
            .source
            .clone();

        let file_path = Self::get_file_path(&source)?;

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

        // Update registry
        if let Some(mut entry) = self.sources.get_mut(&key) {
            entry.last_synced_at = Some(synced_at);
            entry.has_pending_changes = false;
            entry.last_error = None;
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
        let key = Self::key(tenant_id, session_id);

        Ok(self.sources.get(&key).map(|entry| SyncStatus {
            session_id: session_id.to_string(),
            source: entry.source.clone(),
            auto_sync_enabled: entry.auto_sync,
            last_synced_at: entry.last_synced_at,
            has_pending_changes: entry.has_pending_changes,
            last_error: entry.last_error.clone(),
        }))
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_sources(&self, tenant_id: &str) -> Result<Vec<SyncStatus>, StorageError> {
        let mut results = Vec::new();

        for entry in self.sources.iter() {
            let (key, registered) = entry.pair();
            if key.0 == tenant_id {
                results.push(SyncStatus {
                    session_id: key.1.clone(),
                    source: registered.source.clone(),
                    auto_sync_enabled: registered.auto_sync,
                    last_synced_at: registered.last_synced_at,
                    has_pending_changes: registered.has_pending_changes,
                    last_error: registered.last_error.clone(),
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
        let key = Self::key(tenant_id, session_id);
        Ok(self
            .sources
            .get(&key)
            .map(|entry| entry.auto_sync)
            .unwrap_or(false))
    }
}

/// Mark a session as having pending changes (for auto-sync tracking).
impl LocalFileSyncBackend {
    #[allow(dead_code)]
    pub fn mark_pending_changes(&self, tenant_id: &str, session_id: &str) {
        let key = Self::key(tenant_id, session_id);
        if let Some(mut entry) = self.sources.get_mut(&key) {
            entry.has_pending_changes = true;
        }
    }

    #[allow(dead_code)]
    pub fn record_sync_error(&self, tenant_id: &str, session_id: &str, error: &str) {
        let key = Self::key(tenant_id, session_id);
        if let Some(mut entry) = self.sources.get_mut(&key) {
            entry.last_error = Some(error.to_string());
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
    use tempfile::TempDir;

    async fn setup() -> (LocalFileSyncBackend, TempDir) {
        let temp_dir = TempDir::new().unwrap();
        let backend = LocalFileSyncBackend::new();
        (backend, temp_dir)
    }

    #[tokio::test]
    async fn test_register_unregister() {
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = temp_dir.path().join("output.docx");

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
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = temp_dir.path().join("output.docx");

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
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";

        // Register multiple sources
        for i in 0..3 {
            let session = format!("session-{}", i);
            let file_path = temp_dir.path().join(format!("output-{}.docx", i));
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
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = temp_dir.path().join("output.docx");

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
        let backend = LocalFileSyncBackend::new();
        let tenant = "test-tenant";
        let session = "test-session";

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
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = temp_dir.path().join("output.docx");
        let new_file_path = temp_dir.path().join("new-output.docx");

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
        let backend = LocalFileSyncBackend::new();
        let tenant = "test-tenant";
        let session = "nonexistent";

        let result = backend.update_source(tenant, session, None, Some(true)).await;
        assert!(result.is_err());
        assert!(result.unwrap_err().to_string().contains("No source registered"));
    }
}
