mod local;

// Re-export from docx-storage-core
pub use docx_storage_core::{SessionIndexEntry, StorageBackend, WalEntry};

pub use local::LocalStorage;
