//! Core traits and types for docx-mcp storage backends.
//!
//! This crate defines the abstractions shared between local and cloud storage implementations:
//! - `StorageBackend`: Session, index, WAL, and checkpoint operations
//! - `SyncBackend`: Auto-save and source synchronization
//! - `WatchBackend`: External change detection
//! - `BrowsableBackend`: Connection browsing and file listing
//! - `LockManager`: Distributed locking for atomic operations

mod browse;
mod error;
mod lock;
mod storage;
mod sync;
mod watch;

pub use browse::{BrowsableBackend, ConnectionInfo, FileEntry, FileListResult};
pub use error::StorageError;
pub use lock::{LockAcquireResult, LockManager};
pub use storage::{
    CheckpointInfo, SessionIndex, SessionIndexEntry, SessionInfo, StorageBackend, WalEntry,
};
pub use sync::{SourceDescriptor, SourceType, SyncBackend, SyncStatus};
pub use watch::{ExternalChangeEvent, ExternalChangeType, SourceMetadata, WatchBackend};
