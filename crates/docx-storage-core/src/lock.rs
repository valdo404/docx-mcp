use std::time::Duration;

use async_trait::async_trait;

use crate::error::StorageError;

/// Result of a lock acquisition attempt.
#[derive(Debug, Clone)]
pub struct LockAcquireResult {
    /// Whether the lock was acquired.
    pub acquired: bool,
}

impl LockAcquireResult {
    /// Create a successful acquisition result.
    pub fn acquired() -> Self {
        Self { acquired: true }
    }

    /// Create a failed acquisition result (lock held by another).
    pub fn not_acquired() -> Self {
        Self { acquired: false }
    }
}

/// Lock manager abstraction for tenant-aware distributed locking.
///
/// Locks are on the pair `(tenant_id, resource_id)` to ensure tenant isolation.
/// The maximum number of concurrent locks = T tenants × F files per tenant.
///
/// Note: This is used internally by atomic index operations. Locking is not
/// exposed to clients - the server handles it transparently.
#[async_trait]
pub trait LockManager: Send + Sync {
    /// Attempt to acquire a lock on `(tenant_id, resource_id)`.
    ///
    /// # Arguments
    /// * `tenant_id` - Tenant identifier for isolation
    /// * `resource_id` - Resource to lock (e.g., session_id)
    /// * `holder_id` - Unique identifier for this lock holder (UUID recommended)
    /// * `ttl` - Time-to-live for the lock to prevent orphaned locks
    ///
    /// # Returns
    /// * `Ok(result)` - Lock result with acquisition status
    async fn acquire(
        &self,
        tenant_id: &str,
        resource_id: &str,
        holder_id: &str,
        ttl: Duration,
    ) -> Result<LockAcquireResult, StorageError>;

    /// Release a lock.
    ///
    /// The lock is only released if `holder_id` matches the current holder.
    /// Silently succeeds if the lock doesn't exist or is held by someone else.
    async fn release(
        &self,
        tenant_id: &str,
        resource_id: &str,
        holder_id: &str,
    ) -> Result<(), StorageError>;
}
