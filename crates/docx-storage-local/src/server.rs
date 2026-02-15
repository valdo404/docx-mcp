use std::path::Path;
use std::sync::Arc;

use crate::browse::LocalBrowsableBackend;
use crate::lock::{FileLock, LockManager};
use crate::storage::{LocalStorage, StorageBackend};
use crate::sync::LocalFileSyncBackend;
use crate::watch::NotifyWatchBackend;
use docx_storage_core::{BrowsableBackend, SyncBackend, WatchBackend};

/// Create all storage backends from a base directory.
/// Shared between the standalone server binary and the embedded staticlib.
pub fn create_backends(
    storage_dir: &Path,
) -> (
    Arc<dyn StorageBackend>,
    Arc<dyn LockManager>,
    Arc<dyn SyncBackend>,
    Arc<dyn WatchBackend>,
    Arc<dyn BrowsableBackend>,
) {
    let storage: Arc<dyn StorageBackend> = Arc::new(LocalStorage::new(storage_dir));
    let lock: Arc<dyn LockManager> = Arc::new(FileLock::new(storage_dir));
    let sync: Arc<dyn SyncBackend> = Arc::new(LocalFileSyncBackend::new(storage.clone()));
    let watch: Arc<dyn WatchBackend> = Arc::new(NotifyWatchBackend::new());
    let browse: Arc<dyn BrowsableBackend> = Arc::new(LocalBrowsableBackend::new());
    (storage, lock, sync, watch, browse)
}
