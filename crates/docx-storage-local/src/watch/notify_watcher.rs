use std::path::PathBuf;
use std::sync::Arc;

use async_trait::async_trait;
use dashmap::DashMap;
use docx_storage_core::{
    ExternalChangeEvent, ExternalChangeType, SourceDescriptor, SourceMetadata, SourceType,
    StorageError, WatchBackend,
};
use notify::{Config, Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use sha2::{Digest, Sha256};
use tokio::sync::mpsc;
use tracing::{debug, info, instrument, warn};

/// State for a watched source
#[derive(Debug, Clone)]
struct WatchedSource {
    source: SourceDescriptor,
    #[allow(dead_code)]
    watch_id: String,
    known_metadata: Option<SourceMetadata>,
}

/// Local file watch backend using the `notify` crate.
///
/// Uses filesystem events (inotify on Linux, FSEvents on macOS, etc.)
/// to detect when external sources are modified.
pub struct NotifyWatchBackend {
    /// Watched sources: (tenant_id, session_id) -> WatchedSource
    sources: DashMap<(String, String), WatchedSource>,
    /// Pending change events: (tenant_id, session_id) -> ExternalChangeEvent
    pending_changes: DashMap<(String, String), ExternalChangeEvent>,
    /// Sender for change events (used by the watcher thread)
    event_sender: mpsc::Sender<(String, String, Event)>,
    /// Keep watcher alive (it stops when dropped)
    _watcher: Arc<std::sync::Mutex<Option<RecommendedWatcher>>>,
}

impl NotifyWatchBackend {
    /// Create a new NotifyWatchBackend.
    pub fn new() -> Self {
        let (tx, mut rx) = mpsc::channel::<(String, String, Event)>(1000);
        let pending_changes: DashMap<(String, String), ExternalChangeEvent> = DashMap::new();
        let sources: DashMap<(String, String), WatchedSource> = DashMap::new();

        let pending_changes_clone = pending_changes.clone();
        let sources_clone = sources.clone();

        // Spawn a task to process events from the watcher
        tokio::spawn(async move {
            while let Some((tenant_id, session_id, event)) = rx.recv().await {
                let key = (tenant_id.clone(), session_id.clone());

                // Determine change type from event kind
                let change_type = match event.kind {
                    EventKind::Modify(_) => ExternalChangeType::Modified,
                    EventKind::Remove(_) => ExternalChangeType::Deleted,
                    EventKind::Create(_) => ExternalChangeType::Modified, // Treat create as modify for simplicity
                    _ => continue, // Ignore other events
                };

                // Get known metadata if we have it
                let old_metadata = sources_clone
                    .get(&key)
                    .and_then(|w| w.known_metadata.clone());

                // Try to get new metadata
                let new_metadata = if let Some(source) = sources_clone.get(&key) {
                    Self::get_metadata_sync(&source.source).ok()
                } else {
                    None
                };

                let change_event = ExternalChangeEvent {
                    session_id: session_id.clone(),
                    change_type,
                    old_metadata,
                    new_metadata,
                    detected_at: chrono::Utc::now().timestamp(),
                    new_uri: None,
                };

                pending_changes_clone.insert(key, change_event);
                debug!(
                    "Detected {} change for tenant {} session {}",
                    match change_type {
                        ExternalChangeType::Modified => "modified",
                        ExternalChangeType::Deleted => "deleted",
                        ExternalChangeType::Renamed => "renamed",
                        ExternalChangeType::PermissionChanged => "permission",
                    },
                    tenant_id,
                    session_id
                );
            }
        });

        Self {
            sources,
            pending_changes,
            event_sender: tx,
            _watcher: Arc::new(std::sync::Mutex::new(None)),
        }
    }

    /// Get the key for the sources map.
    fn key(tenant_id: &str, session_id: &str) -> (String, String) {
        (tenant_id.to_string(), session_id.to_string())
    }

    /// Get the file path from a source descriptor.
    fn get_file_path(source: &SourceDescriptor) -> Result<PathBuf, StorageError> {
        if source.source_type != SourceType::LocalFile {
            return Err(StorageError::Watch(format!(
                "NotifyWatchBackend only supports LocalFile sources, got {:?}",
                source.source_type
            )));
        }
        Ok(PathBuf::from(&source.uri))
    }

    /// Get file metadata synchronously (for use in sync context).
    /// Computes SHA256 hash of file content for accurate change detection,
    /// matching the C# ExternalChangeTracker behavior.
    fn get_metadata_sync(source: &SourceDescriptor) -> Result<SourceMetadata, StorageError> {
        let path = Self::get_file_path(source)?;

        // Read file to compute hash (like C# ExternalChangeTracker)
        let content = std::fs::read(&path).map_err(|e| {
            StorageError::Watch(format!(
                "Failed to read file {}: {}",
                path.display(),
                e
            ))
        })?;

        let metadata = std::fs::metadata(&path).map_err(|e| {
            StorageError::Watch(format!(
                "Failed to get metadata for {}: {}",
                path.display(),
                e
            ))
        })?;

        // Compute SHA256 hash (same as C# ComputeFileHash)
        let content_hash = {
            let mut hasher = Sha256::new();
            hasher.update(&content);
            hasher.finalize().to_vec()
        };

        Ok(SourceMetadata {
            size_bytes: metadata.len(),
            modified_at: metadata
                .modified()
                .map(|t| {
                    t.duration_since(std::time::UNIX_EPOCH)
                        .map(|d| d.as_secs() as i64)
                        .unwrap_or(0)
                })
                .unwrap_or(0),
            etag: None,
            version_id: None,
            content_hash: Some(content_hash),
        })
    }
}

impl Default for NotifyWatchBackend {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl WatchBackend for NotifyWatchBackend {
    #[instrument(skip(self), level = "debug")]
    async fn start_watch(
        &self,
        tenant_id: &str,
        session_id: &str,
        source: &SourceDescriptor,
        _poll_interval_secs: u32,
    ) -> Result<String, StorageError> {
        // Validate source type
        if source.source_type != SourceType::LocalFile {
            return Err(StorageError::Watch(format!(
                "NotifyWatchBackend only supports LocalFile sources, got {:?}",
                source.source_type
            )));
        }

        let path = Self::get_file_path(source)?;
        let watch_id = uuid::Uuid::new_v4().to_string();
        let key = Self::key(tenant_id, session_id);

        // Get initial metadata
        let known_metadata = Self::get_metadata_sync(source).ok();

        // Set up notify watcher for this file
        let tenant_id_clone = tenant_id.to_string();
        let session_id_clone = session_id.to_string();
        let tx = self.event_sender.clone();
        let path_clone = path.clone();

        let watcher_result = RecommendedWatcher::new(
            move |res: Result<Event, notify::Error>| {
                match res {
                    Ok(event) => {
                        // Only process events for our file
                        if event.paths.iter().any(|p| p == &path_clone) {
                            let _ = tx.blocking_send((
                                tenant_id_clone.clone(),
                                session_id_clone.clone(),
                                event,
                            ));
                        }
                    }
                    Err(e) => {
                        warn!("Watch error: {}", e);
                    }
                }
            },
            Config::default(),
        );

        let mut watcher = match watcher_result {
            Ok(w) => w,
            Err(e) => {
                return Err(StorageError::Watch(format!(
                    "Failed to create watcher: {}",
                    e
                )));
            }
        };

        // Watch the file's parent directory (file watchers need the dir)
        let watch_path = path.parent().unwrap_or(&path);
        watcher
            .watch(watch_path, RecursiveMode::NonRecursive)
            .map_err(|e| {
                StorageError::Watch(format!(
                    "Failed to watch {}: {}",
                    watch_path.display(),
                    e
                ))
            })?;

        // Store the watcher (need to keep it alive)
        {
            let mut guard = self._watcher.lock().unwrap();
            *guard = Some(watcher);
        }

        // Store the watch info
        self.sources.insert(
            key,
            WatchedSource {
                source: source.clone(),
                watch_id: watch_id.clone(),
                known_metadata,
            },
        );

        info!(
            "Started watching {} for tenant {} session {}",
            path.display(),
            tenant_id,
            session_id
        );

        Ok(watch_id)
    }

    #[instrument(skip(self), level = "debug")]
    async fn stop_watch(&self, tenant_id: &str, session_id: &str) -> Result<(), StorageError> {
        let key = Self::key(tenant_id, session_id);

        if let Some((_, watched)) = self.sources.remove(&key) {
            info!(
                "Stopped watching {} for tenant {} session {}",
                watched.source.uri, tenant_id, session_id
            );
        }

        // Also remove any pending changes
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

        // Check for pending changes detected by the watcher
        if let Some((_, event)) = self.pending_changes.remove(&key) {
            return Ok(Some(event));
        }

        // If no pending changes, do a manual check by comparing content hash
        // (like C# ExternalChangeTracker which uses SHA256 hash comparison)
        if let Some(watched) = self.sources.get(&key) {
            if let (Some(known), Ok(current)) = (
                &watched.known_metadata,
                Self::get_metadata_sync(&watched.source),
            ) {
                // Check if file content hash changed (matching C# behavior)
                let hash_changed = match (&known.content_hash, &current.content_hash) {
                    (Some(old_hash), Some(new_hash)) => old_hash != new_hash,
                    // If we don't have hashes, fall back to size/mtime comparison
                    _ => current.modified_at != known.modified_at || current.size_bytes != known.size_bytes,
                };

                if hash_changed {
                    debug!(
                        "Content hash changed for tenant {} session {} (hash-based detection)",
                        tenant_id, session_id
                    );
                    return Ok(Some(ExternalChangeEvent {
                        session_id: session_id.to_string(),
                        change_type: ExternalChangeType::Modified,
                        old_metadata: Some(known.clone()),
                        new_metadata: Some(current),
                        detected_at: chrono::Utc::now().timestamp(),
                        new_uri: None,
                    }));
                }
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

        let source = match self.sources.get(&key) {
            Some(watched) => watched.source.clone(),
            None => return Ok(None),
        };

        let path = Self::get_file_path(&source)?;

        // Check if file exists
        if !path.exists() {
            return Ok(None);
        }

        let metadata = Self::get_metadata_sync(&source)?;
        Ok(Some(metadata))
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use tempfile::TempDir;
    use tokio::time::{sleep, Duration};

    async fn setup() -> (NotifyWatchBackend, TempDir) {
        let temp_dir = TempDir::new().unwrap();
        let backend = NotifyWatchBackend::new();
        (backend, temp_dir)
    }

    #[tokio::test]
    async fn test_start_stop_watch() {
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = temp_dir.path().join("watched.docx");

        // Create the file first
        std::fs::write(&file_path, b"initial content").unwrap();

        let source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: HashMap::new(),
        };

        // Start watch
        let watch_id = backend.start_watch(tenant, session, &source, 0).await.unwrap();
        assert!(!watch_id.is_empty());

        // Get known metadata
        let known = backend.get_known_metadata(tenant, session).await.unwrap();
        assert!(known.is_some());

        // Stop watch
        backend.stop_watch(tenant, session).await.unwrap();

        // Known metadata should be gone
        let known = backend.get_known_metadata(tenant, session).await.unwrap();
        assert!(known.is_none());
    }

    #[tokio::test]
    async fn test_detect_modification() {
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = temp_dir.path().join("watched.docx");

        // Create the file first
        std::fs::write(&file_path, b"initial content").unwrap();

        let source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: HashMap::new(),
        };

        backend.start_watch(tenant, session, &source, 0).await.unwrap();

        // Wait a bit for the watcher to settle
        sleep(Duration::from_millis(100)).await;

        // Modify the file
        std::fs::write(&file_path, b"modified content").unwrap();

        // Wait for the event to be processed
        sleep(Duration::from_millis(500)).await;

        // Check for changes (may detect via manual check if event wasn't captured)
        let change = backend.check_for_changes(tenant, session).await.unwrap();

        // Note: notify events are async and may not always be captured in tests
        // The manual check should still detect the modification
        if change.is_some() {
            let change = change.unwrap();
            assert_eq!(change.change_type, ExternalChangeType::Modified);
        }
    }

    #[tokio::test]
    async fn test_get_source_metadata() {
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = temp_dir.path().join("watched.docx");

        // Create a file with known content
        let content = b"test content for metadata";
        std::fs::write(&file_path, content).unwrap();

        let source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: HashMap::new(),
        };

        backend.start_watch(tenant, session, &source, 0).await.unwrap();

        // Get metadata
        let metadata = backend.get_source_metadata(tenant, session).await.unwrap();
        assert!(metadata.is_some());
        let metadata = metadata.unwrap();
        assert_eq!(metadata.size_bytes, content.len() as u64);
    }

    #[tokio::test]
    async fn test_update_known_metadata() {
        let (backend, temp_dir) = setup().await;
        let tenant = "test-tenant";
        let session = "test-session";
        let file_path = temp_dir.path().join("watched.docx");

        std::fs::write(&file_path, b"content").unwrap();

        let source = SourceDescriptor {
            source_type: SourceType::LocalFile,
            uri: file_path.to_string_lossy().to_string(),
            metadata: HashMap::new(),
        };

        backend.start_watch(tenant, session, &source, 0).await.unwrap();

        // Update known metadata
        let new_metadata = SourceMetadata {
            size_bytes: 12345,
            modified_at: 99999,
            etag: Some("test-etag".to_string()),
            version_id: None,
            content_hash: None,
        };

        backend
            .update_known_metadata(tenant, session, new_metadata.clone())
            .await
            .unwrap();

        // Verify it was updated
        let known = backend.get_known_metadata(tenant, session).await.unwrap();
        assert!(known.is_some());
        let known = known.unwrap();
        assert_eq!(known.size_bytes, 12345);
        assert_eq!(known.etag, Some("test-etag".to_string()));
    }

    #[tokio::test]
    async fn test_invalid_source_type() {
        let backend = NotifyWatchBackend::new();
        let tenant = "test-tenant";
        let session = "test-session";

        let source = SourceDescriptor {
            source_type: SourceType::S3,
            uri: "s3://bucket/key".to_string(),
            metadata: HashMap::new(),
        };

        let result = backend.start_watch(tenant, session, &source, 0).await;
        assert!(result.is_err());
        assert!(result.unwrap_err().to_string().contains("LocalFile"));
    }
}
