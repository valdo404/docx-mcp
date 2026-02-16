//! Per-tenant MCP session registry with recovery coordination.
//!
//! The backend .NET MCP server keeps transport sessions in memory.
//! When it restarts, those sessions are lost and clients get 404.
//! This registry tracks the current backend session ID per tenant
//! and coordinates recovery (re-initialize) when a 404 is detected.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use tokio::sync::{Mutex as AsyncMutex, OwnedMutexGuard, RwLock};

/// Tracks the current backend MCP session ID for each tenant
/// and serializes recovery attempts per tenant.
pub struct SessionRegistry {
    inner: Mutex<HashMap<String, Arc<TenantEntry>>>,
}

struct TenantEntry {
    /// Current backend session ID (read-heavy, write-rare).
    session_id: RwLock<Option<String>>,
    /// Serializes re-initialization attempts so only one request
    /// performs the initialize handshake per tenant.
    recovery_lock: Arc<AsyncMutex<()>>,
}

impl SessionRegistry {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(HashMap::new()),
        }
    }

    /// Get or create the entry for a tenant.
    fn entry(&self, tenant_id: &str) -> Arc<TenantEntry> {
        let mut map = self.inner.lock().expect("session registry poisoned");
        map.entry(tenant_id.to_string())
            .or_insert_with(|| {
                Arc::new(TenantEntry {
                    session_id: RwLock::new(None),
                    recovery_lock: Arc::new(AsyncMutex::new(())),
                })
            })
            .clone()
    }

    /// Get the current backend session ID for a tenant (if any).
    pub async fn get_session_id(&self, tenant_id: &str) -> Option<String> {
        let entry = self.entry(tenant_id);
        let guard = entry.session_id.read().await;
        guard.clone()
    }

    /// Store a new backend session ID for a tenant.
    pub async fn set_session_id(&self, tenant_id: &str, session_id: String) {
        let entry = self.entry(tenant_id);
        *entry.session_id.write().await = Some(session_id);
    }

    /// Clear the session ID for a tenant (e.g. after detecting 404).
    pub async fn invalidate(&self, tenant_id: &str) {
        let entry = self.entry(tenant_id);
        *entry.session_id.write().await = None;
    }

    /// Acquire the recovery lock for a tenant. Only one recovery
    /// attempt proceeds at a time; others wait and then check if
    /// a new session ID was already established.
    pub async fn acquire_recovery_lock(&self, tenant_id: &str) -> OwnedMutexGuard<()> {
        let entry = self.entry(tenant_id);
        let lock = Arc::clone(&entry.recovery_lock);
        lock.lock_owned().await
    }
}
