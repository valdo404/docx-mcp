use std::collections::hash_map::Entry;
use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Duration;

use async_trait::async_trait;
use docx_storage_core::{LockAcquireResult, LockManager, StorageError};
use fs2::FileExt;
use tracing::{debug, instrument};

/// File-based lock manager using OS-level exclusive file locking.
///
/// This mimics the C# implementation that uses FileShare.None:
/// - Opens lock file with exclusive access (flock on Unix, LockFile on Windows)
/// - Holds the file handle while lock is held
/// - Closing the handle releases the lock
/// - Process crash automatically releases lock (OS closes file descriptors)
///
/// Lock files are stored at:
/// `{base_dir}/{tenant_id}/locks/{resource_id}.lock`
#[derive(Debug)]
pub struct FileLock {
    base_dir: PathBuf,
    /// Active lock handles: (tenant_id, resource_id) -> (holder_id, File)
    handles: Mutex<HashMap<(String, String), (String, File)>>,
}

impl FileLock {
    /// Create a new FileLock with the given base directory.
    pub fn new(base_dir: impl AsRef<Path>) -> Self {
        Self {
            base_dir: base_dir.as_ref().to_path_buf(),
            handles: Mutex::new(HashMap::new()),
        }
    }

    /// Get the locks directory for a tenant.
    fn locks_dir(&self, tenant_id: &str) -> PathBuf {
        self.base_dir.join(tenant_id).join("locks")
    }

    /// Get the path to a lock file.
    fn lock_path(&self, tenant_id: &str, resource_id: &str) -> PathBuf {
        self.locks_dir(tenant_id).join(format!("{}.lock", resource_id))
    }

    /// Ensure the locks directory exists.
    fn ensure_locks_dir(&self, tenant_id: &str) -> Result<(), StorageError> {
        let dir = self.locks_dir(tenant_id);
        std::fs::create_dir_all(&dir).map_err(|e| {
            StorageError::Io(format!("Failed to create locks dir {}: {}", dir.display(), e))
        })?;
        Ok(())
    }
}

#[async_trait]
impl LockManager for FileLock {
    #[instrument(skip(self), level = "debug")]
    async fn acquire(
        &self,
        tenant_id: &str,
        resource_id: &str,
        holder_id: &str,
        _ttl: Duration, // TTL not needed - OS handles cleanup on process exit
    ) -> Result<LockAcquireResult, StorageError> {
        self.ensure_locks_dir(tenant_id)?;
        let path = self.lock_path(tenant_id, resource_id);
        let key = (tenant_id.to_string(), resource_id.to_string());

        // Check if we already hold this lock
        {
            let handles = self.handles.lock().unwrap();
            if let Some((existing_holder, _)) = handles.get(&key) {
                if existing_holder == holder_id {
                    debug!(
                        "Lock on {}/{} already held by {}",
                        tenant_id, resource_id, holder_id
                    );
                    return Ok(LockAcquireResult::acquired());
                } else {
                    // Different holder in same process - shouldn't happen but handle it
                    debug!(
                        "Lock on {}/{} held by {} (requested by {})",
                        tenant_id, resource_id, existing_holder, holder_id
                    );
                    return Ok(LockAcquireResult::not_acquired());
                }
            }
        }

        // Try to open and lock the file
        let file = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(&path)
            .map_err(|e| StorageError::Io(format!("Failed to open lock file: {}", e)))?;

        // Try non-blocking exclusive lock
        match file.try_lock_exclusive() {
            Ok(()) => {
                // Got the lock - store the handle
                let mut handles = self.handles.lock().unwrap();
                handles.insert(key, (holder_id.to_string(), file));
                debug!(
                    "Acquired lock on {}/{} for {}",
                    tenant_id, resource_id, holder_id
                );
                Ok(LockAcquireResult::acquired())
            }
            Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                // Lock held by another process
                debug!(
                    "Lock on {}/{} held by another process (requested by {})",
                    tenant_id, resource_id, holder_id
                );
                Ok(LockAcquireResult::not_acquired())
            }
            Err(e) => Err(StorageError::Io(format!("Failed to acquire lock: {}", e))),
        }
    }

    #[instrument(skip(self), level = "debug")]
    async fn release(
        &self,
        tenant_id: &str,
        resource_id: &str,
        holder_id: &str,
    ) -> Result<(), StorageError> {
        let key = (tenant_id.to_string(), resource_id.to_string());

        let mut handles = self.handles.lock().unwrap();
        match handles.entry(key) {
            Entry::Occupied(entry) => {
                let (existing_holder, _) = entry.get();
                if existing_holder == holder_id {
                    // Remove and drop the file handle - this releases the lock
                    let (_, file) = entry.remove();
                    // Explicitly unlock before dropping (not strictly necessary but clean)
                    let _ = fs2::FileExt::unlock(&file);
                    debug!(
                        "Released lock on {}/{} by {}",
                        tenant_id, resource_id, holder_id
                    );
                } else {
                    debug!(
                        "Cannot release lock on {}/{}: held by {} not {}",
                        tenant_id, resource_id, existing_holder, holder_id
                    );
                }
            }
            Entry::Vacant(_) => {
                debug!(
                    "Lock on {}/{} not found for release by {}",
                    tenant_id, resource_id, holder_id
                );
            }
        }

        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    fn setup() -> (FileLock, TempDir) {
        let temp_dir = TempDir::new().unwrap();
        let lock = FileLock::new(temp_dir.path());
        (lock, temp_dir)
    }

    #[tokio::test]
    async fn test_acquire_release() {
        let (lock_mgr, _temp) = setup();
        let tenant = "test-tenant";
        let resource = "session-1";
        let holder = "holder-1";
        let ttl = Duration::from_secs(60);

        // Acquire lock
        let result = lock_mgr.acquire(tenant, resource, holder, ttl).await.unwrap();
        assert!(result.acquired);

        // Same holder can re-acquire (idempotent)
        let result2 = lock_mgr.acquire(tenant, resource, holder, ttl).await.unwrap();
        assert!(result2.acquired);

        // Different holder in same process cannot acquire
        let result3 = lock_mgr.acquire(tenant, resource, "holder-2", ttl).await.unwrap();
        assert!(!result3.acquired);

        // Release lock
        lock_mgr.release(tenant, resource, holder).await.unwrap();

        // Now holder-2 can acquire
        let result4 = lock_mgr.acquire(tenant, resource, "holder-2", ttl).await.unwrap();
        assert!(result4.acquired);
    }

    #[tokio::test]
    async fn test_release_not_owner() {
        let (lock_mgr, _temp) = setup();
        let tenant = "test-tenant";
        let resource = "session-1";
        let ttl = Duration::from_secs(60);

        // holder-1 acquires
        lock_mgr.acquire(tenant, resource, "holder-1", ttl).await.unwrap();

        // holder-2 tries to release (should be no-op)
        lock_mgr.release(tenant, resource, "holder-2").await.unwrap();

        // Lock should still be held by holder-1
        let result = lock_mgr.acquire(tenant, resource, "holder-1", ttl).await.unwrap();
        assert!(result.acquired); // Can re-acquire (we still hold it)

        // holder-2 still cannot acquire
        let result2 = lock_mgr.acquire(tenant, resource, "holder-2", ttl).await.unwrap();
        assert!(!result2.acquired);
    }

    #[tokio::test]
    async fn test_tenant_isolation() {
        let (lock_mgr, _temp) = setup();
        let ttl = Duration::from_secs(60);

        // tenant-a acquires
        let result1 = lock_mgr.acquire("tenant-a", "session-1", "holder", ttl).await.unwrap();
        assert!(result1.acquired);

        // tenant-b can acquire same resource name (different tenant)
        let result2 = lock_mgr.acquire("tenant-b", "session-1", "holder", ttl).await.unwrap();
        assert!(result2.acquired);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn test_concurrent_locking() {
        use std::sync::Arc;
        use tokio::sync::Barrier;

        let (lock_mgr, _temp) = setup();
        let lock_mgr = Arc::new(lock_mgr);
        let tenant = "test-tenant";
        let resource = "shared-resource";
        let ttl = Duration::from_secs(30);

        const NUM_TASKS: usize = 10;
        let barrier = Arc::new(Barrier::new(NUM_TASKS));
        let counter = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let mut handles = vec![];

        for i in 0..NUM_TASKS {
            let lock_mgr = Arc::clone(&lock_mgr);
            let barrier = Arc::clone(&barrier);
            let counter = Arc::clone(&counter);
            let holder_id = format!("holder-{}", i);

            let handle = tokio::spawn(async move {
                barrier.wait().await;

                // Try to acquire lock with retries
                let mut acquired = false;
                for attempt in 0..100 {
                    if attempt > 0 {
                        tokio::time::sleep(Duration::from_millis(10 + (attempt * 5) as u64)).await;
                    }
                    let result = lock_mgr
                        .acquire(tenant, resource, &holder_id, ttl)
                        .await
                        .expect("acquire failed");
                    if result.acquired {
                        acquired = true;
                        break;
                    }
                }

                assert!(acquired, "Task {} failed to acquire lock", i);

                // Critical section: increment counter
                counter.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

                // Release lock
                lock_mgr
                    .release(tenant, resource, &holder_id)
                    .await
                    .expect("release failed");

                i
            });

            handles.push(handle);
        }

        // Wait for all tasks
        for handle in handles {
            handle.await.expect("task panicked");
        }

        // All tasks should have completed
        assert_eq!(counter.load(std::sync::atomic::Ordering::SeqCst), NUM_TASKS);
    }
}
