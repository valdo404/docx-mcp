use std::path::{Path, PathBuf};
use std::time::Duration;

use async_trait::async_trait;
use serde::{Deserialize, Serialize};
use tokio::fs;
use tokio::io::AsyncWriteExt;
use tracing::{debug, instrument, warn};

use super::traits::{LockAcquireResult, LockManager, LockReleaseResult, LockRenewResult};
use crate::error::StorageError;

/// File-based lock manager for local deployments.
///
/// Lock files are stored at:
/// `{base_dir}/{tenant_id}/locks/{resource_id}.lock`
///
/// Each lock file contains JSON with holder_id and expiration.
#[derive(Debug, Clone)]
pub struct FileLock {
    base_dir: PathBuf,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct LockFile {
    holder_id: String,
    expires_at: i64,
}

impl FileLock {
    /// Create a new FileLock with the given base directory.
    pub fn new(base_dir: impl AsRef<Path>) -> Self {
        Self {
            base_dir: base_dir.as_ref().to_path_buf(),
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
    async fn ensure_locks_dir(&self, tenant_id: &str) -> Result<(), StorageError> {
        let dir = self.locks_dir(tenant_id);
        fs::create_dir_all(&dir).await.map_err(|e| {
            StorageError::Io(format!("Failed to create locks dir {}: {}", dir.display(), e))
        })?;
        Ok(())
    }

    /// Read the current lock file, if it exists and hasn't expired.
    async fn read_lock(&self, tenant_id: &str, resource_id: &str) -> Option<LockFile> {
        let path = self.lock_path(tenant_id, resource_id);
        match fs::read_to_string(&path).await {
            Ok(content) => {
                match serde_json::from_str::<LockFile>(&content) {
                    Ok(lock) => {
                        let now = chrono::Utc::now().timestamp();
                        if lock.expires_at > now {
                            Some(lock)
                        } else {
                            // Lock expired, clean it up
                            let _ = fs::remove_file(&path).await;
                            None
                        }
                    }
                    Err(e) => {
                        warn!("Failed to parse lock file: {}", e);
                        // Corrupted lock file, remove it
                        let _ = fs::remove_file(&path).await;
                        None
                    }
                }
            }
            Err(_) => None,
        }
    }

    /// Write a lock file atomically.
    async fn write_lock(
        &self,
        tenant_id: &str,
        resource_id: &str,
        lock: &LockFile,
    ) -> Result<(), StorageError> {
        self.ensure_locks_dir(tenant_id).await?;
        let path = self.lock_path(tenant_id, resource_id);
        let temp_path = path.with_extension("lock.tmp");

        let content = serde_json::to_string(lock).map_err(|e| {
            StorageError::Serialization(format!("Failed to serialize lock: {}", e))
        })?;

        fs::write(&temp_path, &content).await.map_err(|e| {
            StorageError::Io(format!("Failed to write lock file: {}", e))
        })?;

        fs::rename(&temp_path, &path).await.map_err(|e| {
            StorageError::Io(format!("Failed to rename lock file: {}", e))
        })?;

        Ok(())
    }
}

#[async_trait]
impl LockManager for FileLock {
    fn lock_type(&self) -> &'static str {
        "file"
    }

    #[instrument(skip(self), level = "debug")]
    async fn acquire(
        &self,
        tenant_id: &str,
        resource_id: &str,
        holder_id: &str,
        ttl: Duration,
    ) -> Result<LockAcquireResult, StorageError> {
        self.ensure_locks_dir(tenant_id).await?;
        let path = self.lock_path(tenant_id, resource_id);
        let expires_at = chrono::Utc::now().timestamp() + ttl.as_secs() as i64;

        // Try to atomically create the lock file (O_CREAT | O_EXCL)
        let lock_content = LockFile {
            holder_id: holder_id.to_string(),
            expires_at,
        };
        let content = serde_json::to_string(&lock_content).map_err(|e| {
            StorageError::Serialization(format!("Failed to serialize lock: {}", e))
        })?;

        // Try atomic creation first
        match tokio::fs::OpenOptions::new()
            .write(true)
            .create_new(true) // O_CREAT | O_EXCL - fails if exists
            .open(&path)
            .await
        {
            Ok(mut file) => {
                // Successfully created - we have the lock
                file.write_all(content.as_bytes()).await.map_err(|e| {
                    StorageError::Io(format!("Failed to write lock file: {}", e))
                })?;
                file.flush().await.map_err(|e| {
                    StorageError::Io(format!("Failed to flush lock file: {}", e))
                })?;

                debug!(
                    "Acquired lock on {}/{} for {} (expires at {})",
                    tenant_id, resource_id, holder_id, expires_at
                );
                return Ok(LockAcquireResult {
                    acquired: true,
                    current_holder: None,
                    expires_at,
                });
            }
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                // Lock file exists - check if it's ours or expired
                if let Some(existing) = self.read_lock(tenant_id, resource_id).await {
                    if existing.holder_id == holder_id {
                        // We already hold the lock, renew it
                        self.write_lock(tenant_id, resource_id, &lock_content).await?;

                        debug!(
                            "Renewed existing lock on {}/{} for {}",
                            tenant_id, resource_id, holder_id
                        );
                        return Ok(LockAcquireResult {
                            acquired: true,
                            current_holder: None,
                            expires_at,
                        });
                    }

                    // Someone else holds the lock
                    debug!(
                        "Lock on {}/{} held by {} (requested by {})",
                        tenant_id, resource_id, existing.holder_id, holder_id
                    );
                    return Ok(LockAcquireResult {
                        acquired: false,
                        current_holder: Some(existing.holder_id),
                        expires_at: existing.expires_at,
                    });
                }

                // Lock file exists but is expired/invalid - was cleaned up by read_lock
                // Try again with atomic create
                match tokio::fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&path)
                    .await
                {
                    Ok(mut file) => {
                        file.write_all(content.as_bytes()).await.map_err(|e| {
                            StorageError::Io(format!("Failed to write lock file: {}", e))
                        })?;
                        file.flush().await.map_err(|e| {
                            StorageError::Io(format!("Failed to flush lock file: {}", e))
                        })?;

                        debug!(
                            "Acquired lock on {}/{} for {} after cleanup (expires at {})",
                            tenant_id, resource_id, holder_id, expires_at
                        );
                        return Ok(LockAcquireResult {
                            acquired: true,
                            current_holder: None,
                            expires_at,
                        });
                    }
                    Err(_) => {
                        // Another process grabbed it
                        if let Some(existing) = self.read_lock(tenant_id, resource_id).await {
                            return Ok(LockAcquireResult {
                                acquired: false,
                                current_holder: Some(existing.holder_id),
                                expires_at: existing.expires_at,
                            });
                        }
                        // Shouldn't happen, but fail gracefully
                        return Ok(LockAcquireResult {
                            acquired: false,
                            current_holder: None,
                            expires_at: 0,
                        });
                    }
                }
            }
            Err(e) => {
                return Err(StorageError::Io(format!("Failed to create lock file: {}", e)));
            }
        }
    }

    #[instrument(skip(self), level = "debug")]
    async fn release(
        &self,
        tenant_id: &str,
        resource_id: &str,
        holder_id: &str,
    ) -> Result<LockReleaseResult, StorageError> {
        let path = self.lock_path(tenant_id, resource_id);

        // Check if lock exists
        if let Some(existing) = self.read_lock(tenant_id, resource_id).await {
            if existing.holder_id != holder_id {
                debug!(
                    "Cannot release lock on {}/{}: held by {} not {}",
                    tenant_id, resource_id, existing.holder_id, holder_id
                );
                return Ok(LockReleaseResult {
                    released: false,
                    reason: "not_owner".to_string(),
                });
            }

            // We hold the lock, delete it
            if let Err(e) = fs::remove_file(&path).await {
                if e.kind() != std::io::ErrorKind::NotFound {
                    return Err(StorageError::Io(format!("Failed to delete lock: {}", e)));
                }
            }

            debug!("Released lock on {}/{} by {}", tenant_id, resource_id, holder_id);
            return Ok(LockReleaseResult {
                released: true,
                reason: "ok".to_string(),
            });
        }

        // Lock doesn't exist (might have expired)
        debug!(
            "Lock on {}/{} not found for release by {}",
            tenant_id, resource_id, holder_id
        );
        Ok(LockReleaseResult {
            released: false,
            reason: "not_found".to_string(),
        })
    }

    #[instrument(skip(self), level = "debug")]
    async fn renew(
        &self,
        tenant_id: &str,
        resource_id: &str,
        holder_id: &str,
        ttl: Duration,
    ) -> Result<LockRenewResult, StorageError> {
        if let Some(existing) = self.read_lock(tenant_id, resource_id).await {
            if existing.holder_id != holder_id {
                debug!(
                    "Cannot renew lock on {}/{}: held by {} not {}",
                    tenant_id, resource_id, existing.holder_id, holder_id
                );
                return Ok(LockRenewResult {
                    renewed: false,
                    expires_at: existing.expires_at,
                    reason: "not_owner".to_string(),
                });
            }

            // We hold the lock, renew it
            let expires_at = chrono::Utc::now().timestamp() + ttl.as_secs() as i64;
            let lock = LockFile {
                holder_id: holder_id.to_string(),
                expires_at,
            };
            self.write_lock(tenant_id, resource_id, &lock).await?;

            debug!(
                "Renewed lock on {}/{} for {} (new expiry: {})",
                tenant_id, resource_id, holder_id, expires_at
            );
            return Ok(LockRenewResult {
                renewed: true,
                expires_at,
                reason: "ok".to_string(),
            });
        }

        // Lock doesn't exist
        debug!(
            "Lock on {}/{} not found for renewal by {}",
            tenant_id, resource_id, holder_id
        );
        Ok(LockRenewResult {
            renewed: false,
            expires_at: 0,
            reason: "not_found".to_string(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    async fn setup() -> (FileLock, TempDir) {
        let temp_dir = TempDir::new().unwrap();
        let lock = FileLock::new(temp_dir.path());
        (lock, temp_dir)
    }

    #[tokio::test]
    async fn test_acquire_release() {
        let (lock_mgr, _temp) = setup().await;
        let tenant = "test-tenant";
        let resource = "session-1";
        let holder = "holder-1";
        let ttl = Duration::from_secs(60);

        // Acquire lock
        let result = lock_mgr.acquire(tenant, resource, holder, ttl).await.unwrap();
        assert!(result.acquired);
        assert!(result.current_holder.is_none());

        // Try to acquire same lock with different holder
        let result2 = lock_mgr.acquire(tenant, resource, "holder-2", ttl).await.unwrap();
        assert!(!result2.acquired);
        assert_eq!(result2.current_holder, Some(holder.to_string()));

        // Release lock
        let release = lock_mgr.release(tenant, resource, holder).await.unwrap();
        assert!(release.released);
        assert_eq!(release.reason, "ok");

        // Now holder-2 can acquire
        let result3 = lock_mgr.acquire(tenant, resource, "holder-2", ttl).await.unwrap();
        assert!(result3.acquired);
    }

    #[tokio::test]
    async fn test_renew() {
        let (lock_mgr, _temp) = setup().await;
        let tenant = "test-tenant";
        let resource = "session-1";
        let holder = "holder-1";
        let ttl = Duration::from_secs(60);

        // Acquire lock
        let acquire = lock_mgr.acquire(tenant, resource, holder, ttl).await.unwrap();
        assert!(acquire.acquired);
        let original_expiry = acquire.expires_at;

        // Wait a moment then renew
        tokio::time::sleep(Duration::from_millis(100)).await;
        let renew = lock_mgr.renew(tenant, resource, holder, ttl).await.unwrap();
        assert!(renew.renewed);
        assert!(renew.expires_at >= original_expiry);

        // Cannot renew with wrong holder
        let bad_renew = lock_mgr.renew(tenant, resource, "wrong-holder", ttl).await.unwrap();
        assert!(!bad_renew.renewed);
        assert_eq!(bad_renew.reason, "not_owner");
    }

    #[tokio::test]
    async fn test_release_not_owner() {
        let (lock_mgr, _temp) = setup().await;
        let tenant = "test-tenant";
        let resource = "session-1";
        let ttl = Duration::from_secs(60);

        // holder-1 acquires
        lock_mgr.acquire(tenant, resource, "holder-1", ttl).await.unwrap();

        // holder-2 tries to release
        let release = lock_mgr.release(tenant, resource, "holder-2").await.unwrap();
        assert!(!release.released);
        assert_eq!(release.reason, "not_owner");

        // Lock should still be held by holder-1
        let acquire = lock_mgr.acquire(tenant, resource, "holder-1", ttl).await.unwrap();
        assert!(acquire.acquired); // Re-acquires (renews)
    }

    #[tokio::test]
    async fn test_tenant_isolation() {
        let (lock_mgr, _temp) = setup().await;
        let ttl = Duration::from_secs(60);

        // tenant-a acquires
        lock_mgr.acquire("tenant-a", "session-1", "holder", ttl).await.unwrap();

        // tenant-b can acquire same resource name (different tenant)
        let result = lock_mgr.acquire("tenant-b", "session-1", "holder", ttl).await.unwrap();
        assert!(result.acquired);
    }

    #[tokio::test]
    async fn test_expired_lock() {
        let (lock_mgr, _temp) = setup().await;
        let tenant = "test-tenant";
        let resource = "session-1";

        // Acquire with very short TTL
        let result = lock_mgr
            .acquire(tenant, resource, "holder-1", Duration::from_millis(1))
            .await
            .unwrap();
        assert!(result.acquired);

        // Wait for expiration
        tokio::time::sleep(Duration::from_millis(50)).await;

        // Another holder can now acquire
        let result2 = lock_mgr
            .acquire(tenant, resource, "holder-2", Duration::from_secs(60))
            .await
            .unwrap();
        assert!(result2.acquired);
    }
}
